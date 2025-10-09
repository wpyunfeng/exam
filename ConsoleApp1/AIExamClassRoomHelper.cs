using DTcms.Core.Common.Emums;
using DTcms.Core.Common.Extensions;
using Google.OrTools.Sat;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DTcms.Core.Common.Helpers
{
    /// <summary>
    /// 自动安排班级考场
    /// </summary>
    public static class AIExamClassRoomHelper
    {

        /// <summary>
        /// 自动安排班级考场
        /// </summary>
        public static List<AIExamClassRoomResult> AutoAssignRooms(AIExamClassRoomModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            var classes = model.ModelClassList?.ToList() ?? throw new ArgumentException("ModelClassList is required", nameof(model));
            var rooms = model.ModelRoomList?.ToList() ?? throw new ArgumentException("ModelRoomList is required", nameof(model));

            if (!classes.Any())
            {
                return new List<AIExamClassRoomResult>();
            }

            if (!rooms.Any())
            {
                throw new InvalidOperationException("No rooms available for allocation.");
            }

            // Maintain deterministic ordering based on provided list order for classes.
            var orderedClasses = classes.ToList();

            // Group rooms by building and order by room number (fallback to room id if missing).
            var roomsByBuilding = rooms
                .GroupBy(r => r.BuildingId)
                .ToDictionary(g => g.Key, g => g
                    .OrderBy(r => r.RoomNo ?? r.ModelRoomId)
                    .ThenBy(r => r.ModelRoomId)
                    .ToList());

            // Prepare mapping from room id to room metadata for quick access.
            var roomById = rooms.ToDictionary(r => r.ModelRoomId);

            // Build contiguous segment options for each class.
            var classSegments = new List<List<SegmentOption>>();

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                var examClass = orderedClasses[classIndex];
                var segmentsForClass = new List<SegmentOption>();

                foreach (var building in roomsByBuilding.OrderBy(b => b.Key))
                {
                    var buildingRooms = building.Value;
                    for (int start = 0; start < buildingRooms.Count; start++)
                    {
                        int capacitySum = 0;
                        var roomsInSegment = new List<AIExamClassRoomModelRoom>();
                        for (int end = start; end < buildingRooms.Count; end++)
                        {
                            var room = buildingRooms[end];
                            roomsInSegment.Add(room);
                            capacitySum += room.SeatCount;

                            int roomCount = roomsInSegment.Count;
                            if (capacitySum < examClass.StudentCount)
                            {
                                continue;
                            }

                            if (examClass.StudentCount < roomCount)
                            {
                                continue;
                            }

                            var allocations = ComputeEvenAllocations(examClass.StudentCount, roomsInSegment);
                            if (allocations == null)
                            {
                                continue;
                            }

                            var segmentOption = new SegmentOption
                            {
                                BuildingId = building.Key,
                                Rooms = roomsInSegment.Select(r => r.ModelRoomId).ToList(),
                                TotalCapacity = capacitySum,
                                RoomAllocations = allocations
                            };

                            segmentsForClass.Add(segmentOption);
                        }
                    }
                }

                if (!segmentsForClass.Any())
                {
                    throw new InvalidOperationException($"No feasible room assignment found for class {examClass.ModelClassId}.");
                }

                segmentsForClass = segmentsForClass
                    .OrderBy(s => s.BuildingId)
                    .ThenBy(s => string.Join("_", s.Rooms.OrderBy(id => id)))
                    .ToList();

                classSegments.Add(segmentsForClass);
            }

            var cpModel = new CpModel();

            // Decision variables: whether a class chooses a specific segment.
            var segmentVariables = new BoolVar[orderedClasses.Count][];
            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                var segmentsForClass = classSegments[classIndex];
                segmentVariables[classIndex] = new BoolVar[segmentsForClass.Count];
                var segmentSelection = new List<BoolVar>();

                for (int segmentIndex = 0; segmentIndex < segmentsForClass.Count; segmentIndex++)
                {
                    var boolVar = cpModel.NewBoolVar($"class_{classIndex}_segment_{segmentIndex}");
                    segmentVariables[classIndex][segmentIndex] = boolVar;
                    segmentSelection.Add(boolVar);
                }

                cpModel.Add(LinearExpr.Sum(segmentSelection) == 1);
            }

            // Track deterministic usage of rooms per class.
            var classRoomUsage = new Dictionary<(int classIndex, int roomId), BoolVar>();

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                foreach (var room in rooms)
                {
                    var containingSegments = new List<BoolVar>();

                    for (int segmentIndex = 0; segmentIndex < classSegments[classIndex].Count; segmentIndex++)
                    {
                        if (!classSegments[classIndex][segmentIndex].RoomAllocations.ContainsKey(room.ModelRoomId))
                        {
                            continue;
                        }

                        containingSegments.Add(segmentVariables[classIndex][segmentIndex]);
                    }

                    if (!containingSegments.Any())
                    {
                        continue;
                    }

                    var usageVar = cpModel.NewBoolVar($"class_{classIndex}_room_{room.ModelRoomId}");
                    classRoomUsage[(classIndex, room.ModelRoomId)] = usageVar;

                    cpModel.Add(usageVar == LinearExpr.Sum(containingSegments));
                }
            }

            // Seat capacity constraints per room using precomputed allocations.
            var roomAssignedVars = new Dictionary<int, IntVar>();

            foreach (var room in rooms)
            {
                var assignmentTerms = new List<LinearExpr>();

                for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
                {
                    for (int segmentIndex = 0; segmentIndex < classSegments[classIndex].Count; segmentIndex++)
                    {
                        var segment = classSegments[classIndex][segmentIndex];
                        if (!segment.RoomAllocations.TryGetValue(room.ModelRoomId, out var count))
                        {
                            continue;
                        }

                        var segVar = segmentVariables[classIndex][segmentIndex];
                        assignmentTerms.Add(segVar * count);
                    }
                }

                var totalVar = cpModel.NewIntVar(0, room.SeatCount, $"room_{room.ModelRoomId}_assigned_total");
                roomAssignedVars[room.ModelRoomId] = totalVar;

                if (assignmentTerms.Any())
                {
                    cpModel.Add(totalVar == LinearExpr.Sum(assignmentTerms));
                }
                else
                {
                    cpModel.Add(totalVar == 0);
                }

                cpModel.Add(totalVar <= room.SeatCount);
            }

            // Grade constraints: at most one class per grade per room and at most two grades per room.
            var grades = orderedClasses.Select(c => c.Grade).Distinct().OrderBy(g => g).ToList();

            foreach (var room in rooms)
            {
                var gradeVarsForRoom = new List<BoolVar>();

                foreach (var grade in grades)
                {
                    var relevantClassUsageVars = new List<BoolVar>();

                    for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
                    {
                        if (orderedClasses[classIndex].Grade != grade)
                        {
                            continue;
                        }

                        if (classRoomUsage.TryGetValue((classIndex, room.ModelRoomId), out var usageVar))
                        {
                            relevantClassUsageVars.Add(usageVar);
                        }
                    }

                    if (!relevantClassUsageVars.Any())
                    {
                        continue;
                    }

                    cpModel.Add(LinearExpr.Sum(relevantClassUsageVars) <= 1);

                    var gradeUsageVar = cpModel.NewBoolVar($"room_{room.ModelRoomId}_grade_{grade}");
                    gradeVarsForRoom.Add(gradeUsageVar);

                    foreach (var usage in relevantClassUsageVars)
                    {
                        cpModel.AddImplication(usage, gradeUsageVar);
                    }

                    cpModel.Add(gradeUsageVar <= LinearExpr.Sum(relevantClassUsageVars));
                }

                if (gradeVarsForRoom.Any())
                {
                    cpModel.Add(LinearExpr.Sum(gradeVarsForRoom) <= 2);
                }
            }

            // Objective: minimize unused seats across all rooms.
            var objectiveTerms = new List<LinearExpr>();

            foreach (var room in rooms)
            {
                var assignedVar = roomAssignedVars[room.ModelRoomId];
                var slackVar = cpModel.NewIntVar(0, room.SeatCount, $"room_{room.ModelRoomId}_slack");
                cpModel.Add(assignedVar + slackVar == room.SeatCount);
                objectiveTerms.Add(slackVar * 1000 + assignedVar);
            }

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                for (int segmentIndex = 0; segmentIndex < classSegments[classIndex].Count; segmentIndex++)
                {
                    var segVar = segmentVariables[classIndex][segmentIndex];
                    objectiveTerms.Add(segVar * (segmentIndex + 1));
                }
            }

            if (objectiveTerms.Any())
            {
                cpModel.Minimize(LinearExpr.Sum(objectiveTerms));
            }

            var solver = new CpSolver
            {
                StringParameters = "max_time_in_seconds:30"
            };

            var status = solver.Solve(cpModel);

            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                throw new InvalidOperationException("Unable to find a feasible room assignment satisfying all constraints.");
            }

            var results = new List<AIExamClassRoomResult>();

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                int chosenSegmentIndex = -1;
                for (int segmentIndex = 0; segmentIndex < classSegments[classIndex].Count; segmentIndex++)
                {
                    if (solver.BooleanValue(segmentVariables[classIndex][segmentIndex]))
                    {
                        chosenSegmentIndex = segmentIndex;
                        break;
                    }
                }

                if (chosenSegmentIndex < 0)
                {
                    throw new InvalidOperationException($"Solver failed to assign a segment for class {orderedClasses[classIndex].ModelClassId}.");
                }

                var chosenSegment = classSegments[classIndex][chosenSegmentIndex];
                var orderedRooms = chosenSegment.Rooms
                    .Select(roomId => roomById[roomId])
                    .OrderBy(r => r.RoomNo ?? r.ModelRoomId)
                    .ThenBy(r => r.ModelRoomId)
                    .ToList();

                foreach (var room in orderedRooms)
                {
                    if (!chosenSegment.RoomAllocations.TryGetValue(room.ModelRoomId, out var assigned) || assigned <= 0)
                    {
                        continue;
                    }

                    results.Add(new AIExamClassRoomResult
                    {
                        ModelClassId = orderedClasses[classIndex].ModelClassId,
                        ModelRoomId = room.ModelRoomId,
                        ExamStudentCount = assigned
                    });
                }
            }

            return results;
        }

        private class SegmentOption
        {
            public int BuildingId { get; set; }

            public List<int> Rooms { get; set; } = new List<int>();

            public int TotalCapacity { get; set; }

            public Dictionary<int, int> RoomAllocations { get; set; } = new Dictionary<int, int>();
        }

        private static Dictionary<int, int>? ComputeEvenAllocations(int studentCount, List<AIExamClassRoomModelRoom> rooms)
        {
            int roomCount = rooms.Count;
            if (roomCount == 0)
            {
                return null;
            }

            if (studentCount < roomCount)
            {
                return null;
            }

            int baseShare = studentCount / roomCount;
            int remainder = studentCount % roomCount;

            if (baseShare <= 0)
            {
                return null;
            }

            var allocations = new int[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                allocations[i] = baseShare;
            }

            var extraOrder = rooms
                .Select((room, index) => new { room, index })
                .OrderByDescending(x => x.room.SeatCount)
                .ThenBy(x => x.index)
                .ToList();

            for (int i = 0; i < remainder; i++)
            {
                allocations[extraOrder[i].index] += 1;
            }

            for (int i = 0; i < roomCount; i++)
            {
                if (allocations[i] > rooms[i].SeatCount)
                {
                    return null;
                }
            }

            var allocationMap = new Dictionary<int, int>();
            for (int i = 0; i < roomCount; i++)
            {
                allocationMap[rooms[i].ModelRoomId] = allocations[i];
            }

            return allocationMap;
        }

    }
    public class AIExamClassRoomModel
    {

        /// <summary>
        /// 班级列表
        /// </summary>
        public List<AIExamClassRoomModelClass>? ModelClassList { get; set; }
        /// <summary>
        /// 考场列表
        /// </summary>
        public List<AIExamClassRoomModelRoom>? ModelRoomList { get; set; }

    }

    public class AIExamClassRoomModelClass
    {
        /// <summary>
        /// 班级年级
        /// </summary>
        public int Grade { get; set; }
        /// <summary>
        /// 班级id
        /// </summary>
        public int ModelClassId { get; set; }
        /// <summary>
        /// 班级名称
        /// </summary>
        public string? ModelClassName { get; set; }
        /// <summary>
        /// 班级人数
        /// </summary>
        public int StudentCount { get; set; }
    }

    public class AIExamClassRoomModelRoom
    {
        /// <summary>
        /// 考场id
        /// </summary>
        public int ModelRoomId { get; set; }
        /// <summary>
        /// 考场名称
        /// </summary>
        public string? ModelRoomName { get; set; }
        /// <summary>
        /// 教学楼id
        /// </summary>
        public int BuildingId { get; set; }
        /// <summary>
        /// 考场编号
        /// </summary>
        public int? RoomNo { get; set; }
        /// <summary>
        /// 座位数
        /// </summary>
        public int SeatCount { get; set; }
    }

    public class AIExamClassRoomResult
    {
        /// <summary>
        /// 班级id
        /// </summary>
        public int ModelClassId { get; set; }

        /// <summary>
        /// 考场id
        /// </summary>
        public int ModelRoomId { get; set; }

        /// <summary>
        /// 考场分配的考生人数
        /// </summary>
        public int ExamStudentCount { get; set; }

    }
}
