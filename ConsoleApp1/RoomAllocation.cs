using Google.OrTools.Sat;
using System.Collections.Generic;
using System.Linq;

namespace DTcms.Core.Common.Helpers
{
    public static partial class AIExamHelper
    {
        private static SlotSolveOutcome AllocateRooms(
            IEnumerable<AIExamModelClass> classes,
            IEnumerable<AIExamModelRoom> rooms,
            Dictionary<int, ClassRoomPreference> classPreferences)
        {
            var roomById = rooms.ToDictionary(r => r.ModelRoomId);

            // Snapshot the original preferences so they can be restored when no relaxation was required.
            var originalPreferences = classPreferences.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Clone());

            var requests = BuildSlotClassRequests(classes, classPreferences, roomById);

            var outcome = SolveSlot(requests, roomById);
            var wasRelaxed = false;

            if (outcome.Status == CpSolverStatus.Infeasible)
            {
                wasRelaxed = true;
                // First relaxation: expand to rooms in the preferred buildings.
                requests = RelaxToPreferredBuildings(requests, classPreferences, roomById);
                outcome = SolveSlot(requests, roomById);
            }

            if (outcome.Status == CpSolverStatus.Infeasible)
            {
                // Final relaxation: expand to every available room.
                requests = requests
                    .Select(r => r.WithCandidateRooms(roomById.Values))
                    .ToList();
                outcome = SolveSlot(requests, roomById);
            }

            if (outcome.Status != CpSolverStatus.Feasible &&
                outcome.Status != CpSolverStatus.Optimal)
            {
                return outcome;
            }

            foreach (var assignment in outcome.Assignments)
            {
                var classId = assignment.Key;
                var roomId = assignment.Value;
                var room = roomById[roomId];

                if (!classPreferences.TryGetValue(classId, out var preference) || wasRelaxed)
                {
                    classPreferences[classId] = new ClassRoomPreference(
                        room.BuildingId,
                        new HashSet<int> { roomId });
                }
                else
                {
                    // Restore the untouched preference when the allocation succeeded without any relaxation.
                    classPreferences[classId] = originalPreferences[classId];
                }
            }

            return outcome;
        }

        private static List<SlotClassRequest> BuildSlotClassRequests(
            IEnumerable<AIExamModelClass> classes,
            Dictionary<int, ClassRoomPreference> classPreferences,
            IReadOnlyDictionary<int, AIExamModelRoom> roomById)
        {
            var requests = new List<SlotClassRequest>();

            foreach (var modelClass in classes)
            {
                if (!classPreferences.TryGetValue(modelClass.ModelClassId, out var preference) ||
                    preference.RoomIds.Count == 0)
                {
                    requests.Add(new SlotClassRequest(
                        modelClass,
                        roomById.Values,
                        null));
                    continue;
                }

                var candidateRooms = preference.RoomIds
                    .Select(id => roomById.TryGetValue(id, out var room) ? room : null)
                    .Where(room => room != null)
                    .Cast<AIExamModelRoom>();

                if (preference.BuildingId.HasValue)
                {
                    candidateRooms = candidateRooms
                        .Where(room => room.BuildingId == preference.BuildingId.Value);
                }

                var materialized = candidateRooms.ToList();
                if (materialized.Count == 0)
                {
                    materialized = roomById.Values.ToList();
                }

                requests.Add(new SlotClassRequest(modelClass, materialized, preference));
            }

            return requests;
        }

        private static List<SlotClassRequest> RelaxToPreferredBuildings(
            IEnumerable<SlotClassRequest> requests,
            Dictionary<int, ClassRoomPreference> classPreferences,
            IReadOnlyDictionary<int, AIExamModelRoom> roomById)
        {
            var relaxed = new List<SlotClassRequest>();

            foreach (var request in requests)
            {
                if (classPreferences.TryGetValue(request.Class.ModelClassId, out var preference) &&
                    preference.BuildingId.HasValue)
                {
                    var expanded = roomById.Values
                        .Where(r => r.BuildingId == preference.BuildingId.Value);

                    relaxed.Add(request.WithCandidateRooms(expanded));
                }
                else
                {
                    relaxed.Add(request);
                }
            }

            return relaxed;
        }

        private static SlotSolveOutcome SolveSlot(
            IReadOnlyList<SlotClassRequest> requests,
            IReadOnlyDictionary<int, AIExamModelRoom> roomById)
        {
            var model = new CpModel();

            var decisionVars = new Dictionary<(int ClassId, int RoomId), IntVar>();

            foreach (var request in requests)
            {
                foreach (var room in request.CandidateRooms)
                {
                    decisionVars[(request.Class.ModelClassId, room.ModelRoomId)] =
                        model.NewBoolVar($"class_{request.Class.ModelClassId}_room_{room.ModelRoomId}");
                }
            }

            foreach (var request in requests)
            {
                var roomVars = request.CandidateRooms
                    .Select(room => decisionVars[(request.Class.ModelClassId, room.ModelRoomId)])
                    .ToArray();

                if (roomVars.Length == 0)
                {
                    return SlotSolveOutcome.Infeasible();
                }

                model.Add(LinearExpr.Sum(roomVars) == 1);
            }

            foreach (var roomGroup in decisionVars.Keys.GroupBy(k => k.RoomId))
            {
                if (!roomById.TryGetValue(roomGroup.Key, out var room))
                {
                    continue;
                }

                var load = new List<LinearExpr>();

                foreach (var key in roomGroup)
                {
                    var request = requests.First(r => r.Class.ModelClassId == key.ClassId);
                    load.Add(decisionVars[key] * request.Class.StudentCount);
                }

                model.Add(LinearExpr.Sum(load) <= room.SeatCount);
            }

            var solver = new CpSolver();
            var status = solver.Solve(model);

            if (status != CpSolverStatus.Feasible && status != CpSolverStatus.Optimal)
            {
                return new SlotSolveOutcome(status, new Dictionary<int, int>());
            }

            var assignments = new Dictionary<int, int>();

            foreach (var request in requests)
            {
                foreach (var room in request.CandidateRooms)
                {
                    var key = (request.Class.ModelClassId, room.ModelRoomId);
                    if (solver.BooleanValue(decisionVars[key]) == 1)
                    {
                        assignments[request.Class.ModelClassId] = room.ModelRoomId;
                        break;
                    }
                }
            }

            return new SlotSolveOutcome(status, assignments);
        }
    }

    internal sealed class SlotClassRequest
    {
        public SlotClassRequest(
            AIExamModelClass modelClass,
            IEnumerable<AIExamModelRoom> candidateRooms,
            ClassRoomPreference? preference)
        {
            Class = modelClass;
            CandidateRooms = candidateRooms.ToList();
            Preference = preference;
        }

        public AIExamModelClass Class { get; }

        public List<AIExamModelRoom> CandidateRooms { get; }

        public ClassRoomPreference? Preference { get; }

        public SlotClassRequest WithCandidateRooms(IEnumerable<AIExamModelRoom> rooms)
        {
            return new SlotClassRequest(Class, rooms, Preference);
        }
    }

    internal sealed class SlotSolveOutcome
    {
        public SlotSolveOutcome(CpSolverStatus status, Dictionary<int, int> assignments)
        {
            Status = status;
            Assignments = assignments;
        }

        public CpSolverStatus Status { get; }

        public Dictionary<int, int> Assignments { get; }

        public static SlotSolveOutcome Infeasible() =>
            new SlotSolveOutcome(CpSolverStatus.Infeasible, new Dictionary<int, int>());
    }

    internal sealed class ClassRoomPreference
    {
        public ClassRoomPreference(int? buildingId, HashSet<int> roomIds)
        {
            BuildingId = buildingId;
            RoomIds = roomIds;
        }

        public int? BuildingId { get; }

        public HashSet<int> RoomIds { get; }

        public ClassRoomPreference Clone() => new ClassRoomPreference(BuildingId, new HashSet<int>(RoomIds));
    }
}
