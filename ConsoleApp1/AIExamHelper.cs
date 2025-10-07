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
    /// 排考帮助类
    /// </summary>
    public static class AIExamHelper
    {
        /// <summary>
        /// 自动排考
        /// </summary>
        public static List<AIExamResult> AutoScheduler(AIExamModel model)
        {
            var error = new StringBuilder();

            if (model == null)
            {
                error.AppendLine("排考参数不能为空。");
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var config = model.Config ?? new AIExamConfig();

            if (model.ModelTimeList == null || model.ModelTimeList.Count == 0)
            {
                error.AppendLine("考试时间段列表不能为空。");
            }
            if (model.ModelClassList == null || model.ModelClassList.Count == 0)
            {
                error.AppendLine("班级列表不能为空。");
            }
            if (model.ModelRoomList == null || model.ModelRoomList.Count == 0)
            {
                error.AppendLine("考场列表不能为空。");
            }
            if (model.ModelSubjectList == null || model.ModelSubjectList.Count == 0)
            {
                error.AppendLine("考试科目列表不能为空。");
            }

            if (error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var timeSlots = BuildTimeSlots(model.ModelTimeList!, error);
            var classes = BuildClasses(model.ModelClassList!, error);
            var rooms = BuildRooms(model.ModelRoomList!, error);
            var teachers = BuildTeachers(model.ModelTeacherList ?? new List<AIExamModelTeacher>());
            var subjects = BuildSubjects(model.ModelSubjectList!, classes, error);

            if (error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            if (subjects.Count == 0)
            {
                return new List<AIExamResult>();
            }

            var subjectTimeAssignments = SolveSubjectTimeAllocation(config, subjects, timeSlots, model, error);
            if (subjectTimeAssignments == null)
            {
                throw new ResponseException(error.ToString(), ErrorCode.SchedulerFail);
            }

            var roomAssignments = AllocateRooms(subjects, rooms, timeSlots, subjectTimeAssignments, model, error);
            if (roomAssignments == null)
            {
                throw new ResponseException(error.ToString(), ErrorCode.SchedulerFail);
            }

            var teacherAssignments = AssignTeachers(teachers, roomAssignments.RoomEvents, model, error);
            if (teacherAssignments == null)
            {
                throw new ResponseException(error.ToString(), ErrorCode.SchedulerFail);
            }

            return BuildResults(roomAssignments, teacherAssignments);

        }

        #region 构建基础数据

        private static List<TimeSlotInfo> BuildTimeSlots(List<AIExamModelTime> timeList, StringBuilder error)
        {
            var result = new List<TimeSlotInfo>();
            for (var i = 0; i < timeList.Count; i++)
            {
                var time = timeList[i];
                if (string.IsNullOrWhiteSpace(time.Date))
                {
                    error.AppendLine($"第 {i + 1} 个考试场次缺少日期信息。");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(time.StartTime) || string.IsNullOrWhiteSpace(time.EndTime))
                {
                    error.AppendLine($"{time.Date} 的考试场次缺少开始或结束时间。");
                    continue;
                }

                if (!TryParseDateTime(time.Date!, time.StartTime!, out var start))
                {
                    error.AppendLine($"无法解析考试场次开始时间：{time.Date} {time.StartTime}");
                    continue;
                }

                if (!TryParseDateTime(time.Date!, time.EndTime!, out var end))
                {
                    error.AppendLine($"无法解析考试场次结束时间：{time.Date} {time.EndTime}");
                    continue;
                }

                if (end <= start)
                {
                    error.AppendLine($"考试场次结束时间必须大于开始时间：{time.Date} {time.StartTime}-{time.EndTime}");
                    continue;
                }

                result.Add(new TimeSlotInfo
                {
                    Index = i,
                    Date = time.Date!,
                    TimeNo = time.TimeNo ?? string.Empty,
                    Start = start,
                    End = end
                });
            }

            return result;
        }

        private static Dictionary<int, ClassInfo> BuildClasses(List<AIExamModelClass> classes, StringBuilder error)
        {
            var result = new Dictionary<int, ClassInfo>();
            foreach (var item in classes)
            {
                if (result.ContainsKey(item.ModelClassId))
                {
                    error.AppendLine($"存在重复的班级ID：{item.ModelClassId}");
                    continue;
                }

                if (item.StudentCount <= 0)
                {
                    error.AppendLine($"班级 {item.ModelClassName ?? item.ModelClassId.ToString()} 学生人数必须大于0。");
                    continue;
                }

                result[item.ModelClassId] = new ClassInfo
                {
                    Class = item,
                    Grade = item.Grade,
                    StudentCount = item.StudentCount
                };
            }

            return result;
        }

        private static Dictionary<int, RoomInfo> BuildRooms(List<AIExamModelRoom> rooms, StringBuilder error)
        {
            var result = new Dictionary<int, RoomInfo>();
            foreach (var room in rooms)
            {
                if (result.ContainsKey(room.ModelRoomId))
                {
                    error.AppendLine($"存在重复的考场ID：{room.ModelRoomId}");
                    continue;
                }

                if (room.SeatCount <= 0)
                {
                    error.AppendLine($"考场 {room.ModelRoomName ?? room.ModelRoomId.ToString()} 座位数必须大于0。");
                    continue;
                }

                if (room.TeacherCount < 0)
                {
                    error.AppendLine($"考场 {room.ModelRoomName ?? room.ModelRoomId.ToString()} 的监考教师数量不能为负数。");
                    continue;
                }

                result[room.ModelRoomId] = new RoomInfo
                {
                    Room = room,
                    RoomId = room.ModelRoomId,
                    BuildingId = room.BuildingId,
                    ExamMode = room.ExamMode ?? string.Empty,
                    SeatCount = room.SeatCount,
                    TeacherCount = Math.Max(0, room.TeacherCount),
                    RoomNo = room.RoomNo
                };
            }

            return result;
        }

        private static List<TeacherInfo> BuildTeachers(List<AIExamModelTeacher> teachers)
        {
            var result = new List<TeacherInfo>();
            foreach (var teacher in teachers)
            {
                result.Add(new TeacherInfo
                {
                    Teacher = teacher,
                    TeacherId = teacher.ModelTeacherId,
                    Gender = teacher.Gender
                });
            }

            return result;
        }

        private static List<SubjectInfo> BuildSubjects(List<AIExamModelSubject> subjects, Dictionary<int, ClassInfo> classes, StringBuilder error)
        {
            var result = new List<SubjectInfo>();
            foreach (var subject in subjects)
            {
                if (subject.ModelSubjectClassList == null || subject.ModelSubjectClassList.Count == 0)
                {
                    error.AppendLine($"科目 {subject.ModelSubjectName ?? subject.ModelSubjectId.ToString()} 没有关联班级。");
                    continue;
                }

                var classList = new List<ClassInfo>();
                foreach (var cls in subject.ModelSubjectClassList)
                {
                    if (!classes.TryGetValue(cls.ModelClassId, out var classInfo))
                    {
                        error.AppendLine($"科目 {subject.ModelSubjectName ?? subject.ModelSubjectId.ToString()} 包含未知班级 {cls.ModelClassId}。");
                        continue;
                    }

                    if (!classList.Contains(classInfo))
                    {
                        classList.Add(classInfo);
                    }
                }

                if (classList.Count == 0)
                {
                    error.AppendLine($"科目 {subject.ModelSubjectName ?? subject.ModelSubjectId.ToString()} 没有可用班级。");
                    continue;
                }

                result.Add(new SubjectInfo
                {
                    Subject = subject,
                    SubjectId = subject.ModelSubjectId,
                    ExamMode = subject.ExamMode ?? string.Empty,
                    Duration = Math.Max(subject.Duration, 0),
                    Priority = subject.Priority,
                    Classes = classList
                });
            }

            return result;
        }

        private static bool TryParseDateTime(string date, string time, out DateTime result)
        {
            if (DateTime.TryParseExact($"{date} {time}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            {
                return true;
            }

            return DateTime.TryParse($"{date} {time}", out result);
        }

        #endregion

        #region 科目与时间段求解

        private static Dictionary<int, int>? SolveSubjectTimeAllocation(AIExamConfig config,
            List<SubjectInfo> subjects,
            List<TimeSlotInfo> timeSlots,
            AIExamModel model,
            StringBuilder error)
        {
            var cpModel = new CpModel();
            var subjectTimeVars = new Dictionary<(int subjectId, int timeIndex), BoolVar>();
            var subjectCandidates = new Dictionary<int, List<int>>();

            foreach (var subject in subjects)
            {
                var candidates = GetCandidateTimeSlots(subject, timeSlots, model, error);
                if (candidates.Count == 0)
                {
                    error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 没有可用的时间段。");
                    return null;
                }

                subjectCandidates[subject.SubjectId] = candidates;
                foreach (var timeIndex in candidates)
                {
                    subjectTimeVars[(subject.SubjectId, timeIndex)] = cpModel.NewBoolVar($"sub_{subject.SubjectId}_t_{timeIndex}");
                }

                cpModel.Add(LinearExpr.Sum(candidates.Select(c => subjectTimeVars[(subject.SubjectId, c)])) == 1);
            }

            ApplyJointSubjectConstraints(subjects, model, cpModel, subjectTimeVars);
            ApplyJointSubjectNotConstraints(model, cpModel, subjectTimeVars);
            ApplyClassDailyLimitConstraint(config, subjects, timeSlots, cpModel, subjectTimeVars);
            ApplyMinIntervalConstraint(config, subjects, timeSlots, cpModel, subjectTimeVars);

            var solver = new CpSolver
            {
                StringParameters = "max_time_in_seconds:120"
            };
            var status = solver.Solve(cpModel);
            if (status != CpSolverStatus.Feasible && status != CpSolverStatus.Optimal)
            {
                error.AppendLine("未能为所有科目找到可行的考试时间安排。");
                return null;
            }

            var result = new Dictionary<int, int>();
            foreach (var subject in subjects)
            {
                foreach (var timeIndex in subjectCandidates[subject.SubjectId])
                {
                    if (solver.BooleanValue(subjectTimeVars[(subject.SubjectId, timeIndex)]))
                    {
                        result[subject.SubjectId] = timeIndex;
                        break;
                    }
                }
            }

            return result;
        }

        private static List<int> GetCandidateTimeSlots(SubjectInfo subject, List<TimeSlotInfo> timeSlots, AIExamModel model, StringBuilder error)
        {
            var candidates = new List<int>();
            var requiredDuration = subject.Duration > 0 ? subject.Duration : 60;
            var specifiedDate = subject.Subject.Date;
            var specifiedStart = subject.Subject.StartTime;

            var jointRule = model.RuleJointSubjectList?.FirstOrDefault(r => r.RuleJointSubjectList != null && r.RuleJointSubjectList.Any(i => i.ModelSubjectId == subject.SubjectId));
            if (jointRule != null)
            {
                if (!string.IsNullOrWhiteSpace(jointRule.Date))
                {
                    specifiedDate = jointRule.Date;
                }
                if (!string.IsNullOrWhiteSpace(jointRule.StartTime))
                {
                    specifiedStart = jointRule.StartTime;
                }
            }

            foreach (var slot in timeSlots)
            {
                if (!string.IsNullOrWhiteSpace(specifiedDate) && !slot.Date.Equals(specifiedDate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(specifiedStart) && !slot.Start.ToString("HH:mm").Equals(specifiedStart, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var duration = (int)Math.Round((slot.End - slot.Start).TotalMinutes);
                if (duration < requiredDuration)
                {
                    continue;
                }

                candidates.Add(slot.Index);
            }

            if (!string.IsNullOrWhiteSpace(specifiedDate) && candidates.Count == 0)
            {
                error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 在指定日期 {specifiedDate} 内没有合适的考试场次。");
            }

            return candidates;
        }

        private static void ApplyJointSubjectConstraints(List<SubjectInfo> subjects, AIExamModel model, CpModel cpModel, Dictionary<(int subjectId, int timeIndex), BoolVar> vars)
        {
            if (model.RuleJointSubjectList == null)
            {
                return;
            }

            foreach (var rule in model.RuleJointSubjectList.Where(r => r.RuleJointSubjectList != null))
            {
                var subjectIds = rule.RuleJointSubjectList!.Select(r => r.ModelSubjectId).Distinct().ToList();
                if (subjectIds.Count <= 1)
                {
                    continue;
                }

                var availableSubjects = subjects.Where(s => subjectIds.Contains(s.SubjectId)).ToList();
                if (availableSubjects.Count <= 1)
                {
                    continue;
                }

                foreach (var timeIndex in vars.Keys.Select(k => k.timeIndex).Distinct())
                {
                    BoolVar? previous = null;
                    foreach (var subjectId in availableSubjects.Select(s => s.SubjectId))
                    {
                        if (!vars.TryGetValue((subjectId, timeIndex), out var current))
                        {
                            continue;
                        }

                        if (previous != null)
                        {
                            cpModel.Add(previous == current);
                        }

                        previous = current;
                    }
                }
            }
        }

        private static void ApplyJointSubjectNotConstraints(AIExamModel model, CpModel cpModel, Dictionary<(int subjectId, int timeIndex), BoolVar> vars)
        {
            if (model.RuleJointSubjectNotList == null)
            {
                return;
            }

            foreach (var rule in model.RuleJointSubjectNotList.Where(r => r.RuleJointSubjectList != null))
            {
                var subjectIds = rule.RuleJointSubjectList!.Select(r => r.ModelSubjectId).Distinct().ToList();
                if (subjectIds.Count <= 1)
                {
                    continue;
                }

                foreach (var (timeIndex, varsInTime) in vars.Where(v => subjectIds.Contains(v.Key.subjectId)).GroupBy(v => v.Key.timeIndex))
                {
                    var list = varsInTime.Select(v => v.Value).ToList();
                    if (list.Count > 1)
                    {
                        cpModel.Add(LinearExpr.Sum(list) <= 1);
                    }
                }
            }
        }

        private static void ApplyClassDailyLimitConstraint(AIExamConfig config, List<SubjectInfo> subjects, List<TimeSlotInfo> timeSlots, CpModel cpModel, Dictionary<(int subjectId, int timeIndex), BoolVar> vars)
        {
            if (config.MaxStudentDaily <= 0)
            {
                return;
            }

            var timeByDate = timeSlots.GroupBy(t => t.Date).ToDictionary(g => g.Key, g => g.Select(t => t.Index).ToList());

            foreach (var cls in subjects.SelectMany(s => s.Classes).Distinct())
            {
                var relatedSubjects = subjects.Where(s => s.Classes.Contains(cls)).ToList();
                if (relatedSubjects.Count <= 0)
                {
                    continue;
                }

                foreach (var date in timeByDate.Keys)
                {
                    var indices = timeByDate[date];
                    var varsInDay = new List<BoolVar>();
                    foreach (var subject in relatedSubjects)
                    {
                        foreach (var timeIndex in indices)
                        {
                            if (vars.TryGetValue((subject.SubjectId, timeIndex), out var variable))
                            {
                                varsInDay.Add(variable);
                            }
                        }
                    }

                    if (varsInDay.Count > 0)
                    {
                        cpModel.Add(LinearExpr.Sum(varsInDay) <= config.MaxStudentDaily);
                    }
                }
            }
        }

        private static void ApplyMinIntervalConstraint(AIExamConfig config, List<SubjectInfo> subjects, List<TimeSlotInfo> timeSlots, CpModel cpModel, Dictionary<(int subjectId, int timeIndex), BoolVar> vars)
        {
            if (config.MinExamInterval <= 0)
            {
                return;
            }

            var timeLookup = timeSlots.ToDictionary(t => t.Index);

            foreach (var cls in subjects.SelectMany(s => s.Classes).Distinct())
            {
                var relatedSubjects = subjects.Where(s => s.Classes.Contains(cls)).ToList();
                for (var i = 0; i < relatedSubjects.Count; i++)
                {
                    for (var j = i + 1; j < relatedSubjects.Count; j++)
                    {
                        var subjectA = relatedSubjects[i];
                        var subjectB = relatedSubjects[j];

                        foreach (var kvpA in vars.Where(v => v.Key.subjectId == subjectA.SubjectId))
                        {
                            foreach (var kvpB in vars.Where(v => v.Key.subjectId == subjectB.SubjectId))
                            {
                                var slotA = timeLookup[kvpA.Key.timeIndex];
                                var slotB = timeLookup[kvpB.Key.timeIndex];
                                if (!slotA.Date.Equals(slotB.Date, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var diff = Math.Abs((slotA.Start - slotB.Start).TotalMinutes);
                                if (diff < config.MinExamInterval)
                                {
                                    cpModel.Add(kvpA.Value + kvpB.Value <= 1);
                                }
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region 教室分配

        private static RoomAssignmentContainer? AllocateRooms(
            List<SubjectInfo> subjects,
            Dictionary<int, RoomInfo> rooms,
            List<TimeSlotInfo> timeSlots,
            Dictionary<int, int> subjectTimeAssignments,
            AIExamModel model,
            StringBuilder error)
        {
            var roomSubjectAllow = BuildSubjectRoomRule(model.RuleRoomSubjectList);
            var roomSubjectBlock = BuildSubjectRoomRule(model.RuleRoomSubjectNotList);

            var container = new RoomAssignmentContainer();
            var eventsLookup = new Dictionary<(int timeIndex, int roomId), RoomEvent>();
            container.EventLookup = eventsLookup;

            var orderedSubjects = subjects
                .Where(s => subjectTimeAssignments.ContainsKey(s.SubjectId))
                .OrderBy(s => s.Priority)
                .ThenByDescending(s => s.Duration)
                .ThenByDescending(s => s.Classes.Sum(c => c.StudentCount))
                .ToList();

            foreach (var subject in orderedSubjects)
            {
                if (!subjectTimeAssignments.TryGetValue(subject.SubjectId, out var timeIndex))
                {
                    error.AppendLine($"缺少科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 的考试时间安排。");
                    return null;
                }

                var slot = timeSlots[timeIndex];
                var candidateRooms = FilterRoomsForSubject(subject, rooms, roomSubjectAllow, roomSubjectBlock, error);
                if (candidateRooms.Count == 0)
                {
                    error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 没有可用的考场。");
                    return null;
                }

                var orderedClasses = subject.Classes
                    .OrderByDescending(c => c.Grade)
                    .ThenByDescending(c => c.StudentCount)
                    .ToList();

                foreach (var cls in orderedClasses)
                {
                    var allocations = FindBestRoomAllocation(cls, subject, slot, candidateRooms, eventsLookup);
                    if (allocations == null || allocations.Count == 0)
                    {
                        error.AppendLine($"无法为科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 的班级 {cls.Class.ModelClassName ?? cls.Class.ModelClassId.ToString()} 分配考场。");
                        return null;
                    }

                    foreach (var allocation in allocations)
                    {
                        var key = (slot.Index, allocation.Room.RoomId);
                        if (!eventsLookup.TryGetValue(key, out var roomEvent))
                        {
                            roomEvent = new RoomEvent
                            {
                                Room = allocation.Room,
                                Slot = slot,
                                Subject = subject
                            };
                            eventsLookup[key] = roomEvent;
                            container.RoomEvents.Add(roomEvent);
                        }
                        else if (roomEvent.Subject.SubjectId != subject.SubjectId)
                        {
                            error.AppendLine($"考场 {allocation.Room.Room.ModelRoomName ?? allocation.Room.RoomId.ToString()} 在同一场次已分配给其它科目。");
                            return null;
                        }

                        var existingShare = roomEvent.ClassShares.FirstOrDefault(s => s.Class.Class.ModelClassId == cls.Class.ModelClassId);
                        if (existingShare == null)
                        {
                            if (roomEvent.ClassShares.Count >= 2)
                            {
                                error.AppendLine($"考场 {allocation.Room.Room.ModelRoomName ?? allocation.Room.RoomId.ToString()} 在场次 {slot.Date} {slot.Start:HH:mm} 已达到班级数量上限。");
                                return null;
                            }

                            if (roomEvent.ClassShares.Any(s => s.Class.Grade == cls.Grade && s.Class.Class.ModelClassId != cls.Class.ModelClassId))
                            {
                                error.AppendLine($"考场 {allocation.Room.Room.ModelRoomName ?? allocation.Room.RoomId.ToString()} 已安排相同年级的其它班级，无法再安排班级 {cls.Class.ModelClassName ?? cls.Class.ModelClassId.ToString()}。");
                                return null;
                            }

                            existingShare = new ClassRoomShare
                            {
                                Class = cls,
                                Students = 0
                            };
                            roomEvent.ClassShares.Add(existingShare);
                        }

                        existingShare.Students += allocation.Students;
                        roomEvent.TotalStudents += allocation.Students;

                        if (roomEvent.TotalStudents > allocation.Room.SeatCount)
                        {
                            error.AppendLine($"考场 {allocation.Room.Room.ModelRoomName ?? allocation.Room.RoomId.ToString()} 的座位数不足。");
                            return null;
                        }

                        container.ClassAssignments.Add(new ClassRoomAssignment
                        {
                            Subject = subject,
                            Class = cls,
                            Room = allocation.Room,
                            Slot = slot,
                            Students = allocation.Students
                        });
                    }

                    var assignedStudents = container.ClassAssignments
                        .Where(a => a.Subject.SubjectId == subject.SubjectId && a.Class.Class.ModelClassId == cls.Class.ModelClassId)
                        .Sum(a => a.Students);

                    if (assignedStudents != cls.StudentCount)
                    {
                        error.AppendLine($"班级 {cls.Class.ModelClassName ?? cls.Class.ModelClassId.ToString()} 未完全分配（当前 {assignedStudents}/{cls.StudentCount}）。");
                        return null;
                    }
                }
            }

            return container;
        }

        private static List<RoomInfo> FilterRoomsForSubject(SubjectInfo subject,
            Dictionary<int, RoomInfo> rooms,
            Dictionary<int, HashSet<int>> roomAllowRules,
            Dictionary<int, HashSet<int>> roomBlockRules,
            StringBuilder error)
        {
            var roomList = new List<RoomInfo>();
            foreach (var room in rooms.Values)
            {
                if (!string.IsNullOrWhiteSpace(subject.ExamMode) && !string.Equals(room.ExamMode, subject.ExamMode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (roomBlockRules.TryGetValue(subject.SubjectId, out var blocked) && blocked.Contains(room.RoomId))
                {
                    continue;
                }

                if (roomAllowRules.TryGetValue(subject.SubjectId, out var allowed) && !allowed.Contains(room.RoomId))
                {
                    continue;
                }

                roomList.Add(room);
            }

            return roomList;
        }

        private static List<RoomAllocation>? FindBestRoomAllocation(ClassInfo cls,
            SubjectInfo subject,
            TimeSlotInfo slot,
            List<RoomInfo> candidateRooms,
            Dictionary<(int timeIndex, int roomId), RoomEvent> eventsLookup)
        {
            var buildingGroups = candidateRooms.GroupBy(r => r.BuildingId).ToList();
            RoomAllocationPlan? bestPlan = null;

            foreach (var buildingGroup in buildingGroups)
            {
                var plan = TryAllocateInBuilding(cls, subject, slot, buildingGroup.ToList(), eventsLookup);
                if (plan == null)
                {
                    continue;
                }

                if (bestPlan == null)
                {
                    bestPlan = plan;
                }
                else
                {
                    if (plan.SeatWaste < bestPlan.SeatWaste
                        || (plan.SeatWaste == bestPlan.SeatWaste && plan.RoomCount < bestPlan.RoomCount)
                        || (plan.SeatWaste == bestPlan.SeatWaste && plan.RoomCount == bestPlan.RoomCount && plan.RoomNoSpread < bestPlan.RoomNoSpread)
                        || (plan.SeatWaste == bestPlan.SeatWaste && plan.RoomCount == bestPlan.RoomCount && plan.RoomNoSpread == bestPlan.RoomNoSpread && plan.MaxRoomCapacity < bestPlan.MaxRoomCapacity))
                    {
                        bestPlan = plan;
                    }
                }
            }

            return bestPlan?.Allocations;
        }

        private static RoomAllocationPlan? TryAllocateInBuilding(ClassInfo cls,
            SubjectInfo subject,
            TimeSlotInfo slot,
            List<RoomInfo> rooms,
            Dictionary<(int timeIndex, int roomId), RoomEvent> eventsLookup)
        {
            var candidates = rooms
                .Select(room => new RoomCandidate
                {
                    Room = room,
                    ExistingEvent = eventsLookup.TryGetValue((slot.Index, room.RoomId), out var evt) ? evt : null
                })
                .Where(c => c.AvailableSeats > 0)
                .OrderBy(c => c.AvailableSeats)
                .ThenBy(c => c.Room.RoomNo ?? int.MaxValue)
                .ThenBy(c => c.Room.SeatCount)
                .ToList();

            var remaining = cls.StudentCount;
            var allocations = new List<RoomAllocation>();

            foreach (var candidate in candidates)
            {
                if (candidate.ExistingEvent != null)
                {
                    if (candidate.ExistingEvent.Subject.SubjectId != subject.SubjectId)
                    {
                        continue;
                    }

                    if (candidate.ExistingEvent.ClassShares.Any(s => s.Class.Grade == cls.Grade && s.Class.Class.ModelClassId != cls.Class.ModelClassId))
                    {
                        continue;
                    }

                    if (candidate.ExistingEvent.ClassShares.Count >= 2 && candidate.ExistingEvent.ClassShares.All(s => s.Class.Class.ModelClassId != cls.Class.ModelClassId))
                    {
                        continue;
                    }
                }

                var available = candidate.AvailableSeats;
                if (available <= 0)
                {
                    continue;
                }

                var assign = Math.Min(available, remaining);
                if (assign <= 0)
                {
                    continue;
                }

                allocations.Add(new RoomAllocation
                {
                    Room = candidate.Room,
                    Students = assign,
                    ExistingEvent = candidate.ExistingEvent
                });

                remaining -= assign;

                if (remaining <= 0)
                {
                    break;
                }
            }

            if (remaining > 0)
            {
                return null;
            }

            var seatWaste = allocations.Sum(a =>
            {
                var used = (a.ExistingEvent?.TotalStudents ?? 0) + a.Students;
                return Math.Max(0, a.Room.SeatCount - used);
            });

            var roomNos = allocations.Where(a => a.Room.RoomNo.HasValue).Select(a => a.Room.RoomNo!.Value).ToList();
            var spread = roomNos.Count > 1 ? roomNos.Max() - roomNos.Min() : 0;

            return new RoomAllocationPlan
            {
                Allocations = allocations,
                SeatWaste = seatWaste,
                RoomCount = allocations.Count,
                MaxRoomCapacity = allocations.Max(a => a.Room.SeatCount),
                RoomNoSpread = spread
            };
        }

        private static Dictionary<int, HashSet<int>> BuildSubjectRoomRule(List<AIExamRuleRoomSubject>? rules)
        {
            var dictionary = new Dictionary<int, HashSet<int>>();
            if (rules == null)
            {
                return dictionary;
            }

            foreach (var rule in rules)
            {
                if (!dictionary.TryGetValue(rule.ModelSubjectId, out var set))
                {
                    set = new HashSet<int>();
                    dictionary[rule.ModelSubjectId] = set;
                }

                set.Add(rule.ModelRoomId);
            }

            return dictionary;
        }

        #endregion

        #region 教师分配

        private static Dictionary<(int timeIndex, int roomId), List<int>>? AssignTeachers(
            List<TeacherInfo> teachers,
            List<RoomEvent> roomEvents,
            AIExamModel model,
            StringBuilder error)
        {
            var result = new Dictionary<(int timeIndex, int roomId), List<int>>();
            foreach (var evt in roomEvents)
            {
                result[(evt.Slot.Index, evt.Room.RoomId)] = new List<int>();
            }

            var eventsRequiringTeacher = roomEvents.Where(e => e.TotalStudents > 0 && e.Room.TeacherCount > 0).ToList();
            if (eventsRequiringTeacher.Count == 0)
            {
                return result;
            }

            if (teachers.Count == 0)
            {
                error.AppendLine("存在需要监考教师的考场，但监考教师列表为空。");
                return null;
            }

            var allowBuilding = BuildTeacherRuleDictionary(model.RuleTeacherBuildingList, r => r.ModelTeacherId, r => r.ModelBuildingId);
            var blockBuilding = BuildTeacherRuleDictionary(model.RuleTeacherBuildingNotList, r => r.ModelTeacherId, r => r.ModelBuildingId);
            var allowClass = BuildTeacherRuleDictionary(model.RuleTeacherClassList, r => r.ModelTeacherId, r => r.ModelClassId);
            var blockClass = BuildTeacherRuleDictionary(model.RuleTeacherClassNotList, r => r.ModelTeacherId, r => r.ModelClassId);
            var allowSubject = BuildTeacherRuleDictionary(model.RuleTeacherSubjectList, r => r.ModelTeacherId, r => r.ModelSubjectId);
            var blockSubject = BuildTeacherRuleDictionary(model.RuleTeacherSubjectNotList, r => r.ModelTeacherId, r => r.ModelSubjectId);
            var unavailable = BuildTeacherUnAvailability(model.RuleTeacherUnTimeList);

            var cpModel = new CpModel();
            var assignmentVars = new Dictionary<(int teacherId, int eventIndex), BoolVar>();
            var teacherEventVars = teachers.ToDictionary(t => t.TeacherId, _ => new List<BoolVar>());

            var eventsByIndex = eventsRequiringTeacher.Select((evt, index) => (evt, index)).ToList();

            foreach (var (evt, index) in eventsByIndex)
            {
                var varsForEvent = new List<BoolVar>();
                var maleVars = new List<BoolVar>();

                foreach (var teacher in teachers)
                {
                    if (!IsTeacherEligibleForEvent(teacher, evt, allowBuilding, blockBuilding, allowClass, blockClass, allowSubject, blockSubject, unavailable))
                    {
                        continue;
                    }

                    var variable = cpModel.NewBoolVar($"teacher_{teacher.TeacherId}_event_{index}");
                    assignmentVars[(teacher.TeacherId, index)] = variable;
                    varsForEvent.Add(variable);
                    teacherEventVars[teacher.TeacherId].Add(variable);

                    if (teacher.Gender == 1)
                    {
                        maleVars.Add(variable);
                    }
                }

                if (varsForEvent.Count < evt.Room.TeacherCount)
                {
                    error.AppendLine($"考场 {evt.Room.Room.ModelRoomName ?? evt.Room.RoomId.ToString()} 在 {evt.Slot.Date} {evt.Slot.Start:HH:mm} 没有足够的可用监考教师。");
                    return null;
                }

                cpModel.Add(LinearExpr.Sum(varsForEvent) == evt.Room.TeacherCount);

                var maleCountVar = cpModel.NewIntVar(0, evt.Room.TeacherCount, $"male_cnt_{index}");
                if (maleVars.Count > 0)
                {
                    cpModel.Add(maleCountVar == LinearExpr.Sum(maleVars));
                }
                else
                {
                    cpModel.Add(maleCountVar == 0);
                }

                var femaleCandidateCount = varsForEvent.Count - maleVars.Count;
                if (maleVars.Count > 0 && femaleCandidateCount > 0)
                {
                    var minMale = evt.Room.TeacherCount / 2;
                    var maxMale = (evt.Room.TeacherCount + 1) / 2;
                    cpModel.Add(maleCountVar >= minMale);
                    cpModel.Add(maleCountVar <= maxMale);
                }
            }

            foreach (var teacher in teachers)
            {
                var vars = teacherEventVars[teacher.TeacherId];
                if (vars.Count == 0)
                {
                    continue;
                }

                var maxOnePerDay = new Dictionary<string, List<BoolVar>>();
                var maxOnePerSlot = new Dictionary<int, List<BoolVar>>();
                foreach (var (evt, index) in eventsByIndex)
                {
                    if (!assignmentVars.TryGetValue((teacher.TeacherId, index), out var variable))
                    {
                        continue;
                    }

                    if (!maxOnePerDay.TryGetValue(evt.Slot.Date, out var list))
                    {
                        list = new List<BoolVar>();
                        maxOnePerDay[evt.Slot.Date] = list;
                    }

                    list.Add(variable);

                    if (!maxOnePerSlot.TryGetValue(evt.Slot.Index, out var slotList))
                    {
                        slotList = new List<BoolVar>();
                        maxOnePerSlot[evt.Slot.Index] = slotList;
                    }

                    slotList.Add(variable);
                }

                foreach (var kv in maxOnePerDay)
                {
                    cpModel.Add(LinearExpr.Sum(kv.Value) <= 1);
                }

                foreach (var kv in maxOnePerSlot)
                {
                    cpModel.Add(LinearExpr.Sum(kv.Value) <= 1);
                }
            }

            var teacherLoads = teachers.ToDictionary(t => t.TeacherId, t => cpModel.NewIntVar(0, eventsRequiringTeacher.Count, $"load_{t.TeacherId}"));
            foreach (var teacher in teachers)
            {
                var vars = teacherEventVars[teacher.TeacherId];
                if (vars.Count == 0)
                {
                    cpModel.Add(teacherLoads[teacher.TeacherId] == 0);
                }
                else
                {
                    cpModel.Add(teacherLoads[teacher.TeacherId] == LinearExpr.Sum(vars));
                }
            }

            var maxLoad = cpModel.NewIntVar(0, eventsRequiringTeacher.Count, "max_teacher_load");
            foreach (var load in teacherLoads.Values)
            {
                cpModel.Add(load <= maxLoad);
            }

            cpModel.Minimize(maxLoad);

            var solver = new CpSolver
            {
                StringParameters = "max_time_in_seconds:120"
            };

            var status = solver.Solve(cpModel);
            if (status != CpSolverStatus.Feasible && status != CpSolverStatus.Optimal)
            {
                error.AppendLine("未能找到满足监考教师约束的方案。");
                return null;
            }

            foreach (var (evt, index) in eventsByIndex)
            {
                var teachersForEvent = new List<int>();
                foreach (var teacher in teachers)
                {
                    if (!assignmentVars.TryGetValue((teacher.TeacherId, index), out var var))
                    {
                        continue;
                    }

                    if (solver.BooleanValue(var))
                    {
                        teachersForEvent.Add(teacher.TeacherId);
                    }
                }

                result[(evt.Slot.Index, evt.Room.RoomId)] = teachersForEvent;
            }

            return result;
        }

        private static Dictionary<int, HashSet<int>> BuildTeacherRuleDictionary<T>(List<T>? rules, Func<T, int> teacherSelector, Func<T, int> targetSelector)
        {
            var dictionary = new Dictionary<int, HashSet<int>>();
            if (rules == null)
            {
                return dictionary;
            }

            foreach (var rule in rules)
            {
                var teacherId = teacherSelector(rule);
                var targetId = targetSelector(rule);
                if (!dictionary.TryGetValue(teacherId, out var set))
                {
                    set = new HashSet<int>();
                    dictionary[teacherId] = set;
                }

                set.Add(targetId);
            }

            return dictionary;
        }

        private static Dictionary<int, List<(DateTime Start, DateTime End)>> BuildTeacherUnAvailability(List<AIExamRuleTeacherUnTime>? rules)
        {
            var dictionary = new Dictionary<int, List<(DateTime Start, DateTime End)>>();
            if (rules == null)
            {
                return dictionary;
            }

            foreach (var rule in rules)
            {
                if (rule.StartTime == null || rule.EndTime == null)
                {
                    continue;
                }

                if (!DateTime.TryParse(rule.StartTime, out var start))
                {
                    continue;
                }

                if (!DateTime.TryParse(rule.EndTime, out var end))
                {
                    continue;
                }

                if (end <= start)
                {
                    continue;
                }

                if (!dictionary.TryGetValue(rule.ModelTeacherId, out var list))
                {
                    list = new List<(DateTime Start, DateTime End)>();
                    dictionary[rule.ModelTeacherId] = list;
                }

                list.Add((start, end));
            }

            return dictionary;
        }

        private static bool IsTeacherEligibleForEvent(TeacherInfo teacher,
            RoomEvent roomEvent,
            Dictionary<int, HashSet<int>> allowBuilding,
            Dictionary<int, HashSet<int>> blockBuilding,
            Dictionary<int, HashSet<int>> allowClass,
            Dictionary<int, HashSet<int>> blockClass,
            Dictionary<int, HashSet<int>> allowSubject,
            Dictionary<int, HashSet<int>> blockSubject,
            Dictionary<int, List<(DateTime Start, DateTime End)>> unavailable)
        {
            var buildingId = roomEvent.Room.BuildingId;
            if (blockBuilding.TryGetValue(teacher.TeacherId, out var blockedBuildings) && blockedBuildings.Contains(buildingId))
            {
                return false;
            }

            if (allowBuilding.TryGetValue(teacher.TeacherId, out var allowedBuildings) && !allowedBuildings.Contains(buildingId))
            {
                return false;
            }

            var classIds = roomEvent.ClassShares.Select(s => s.Class.Class.ModelClassId).ToList();
            if (blockClass.TryGetValue(teacher.TeacherId, out var blockedClasses) && classIds.Any(blockedClasses.Contains))
            {
                return false;
            }

            if (allowClass.TryGetValue(teacher.TeacherId, out var allowedClasses) && classIds.Any(id => !allowedClasses.Contains(id)))
            {
                return false;
            }

            var subjectId = roomEvent.Subject.SubjectId;
            if (blockSubject.TryGetValue(teacher.TeacherId, out var blockedSubjects) && blockedSubjects.Contains(subjectId))
            {
                return false;
            }

            if (allowSubject.TryGetValue(teacher.TeacherId, out var allowedSubjects) && !allowedSubjects.Contains(subjectId))
            {
                return false;
            }

            if (unavailable.TryGetValue(teacher.TeacherId, out var ranges))
            {
                foreach (var range in ranges)
                {
                    if (IsTimeOverlap(roomEvent.StartTime, roomEvent.EndTime, range.Start, range.End))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsTimeOverlap(DateTime startA, DateTime endA, DateTime startB, DateTime endB)
        {
            return startA < endB && startB < endA;
        }

        #endregion

        private static List<AIExamResult> BuildResults(RoomAssignmentContainer container,
            Dictionary<(int timeIndex, int roomId), List<int>> teacherAssignments)
        {
            var results = new List<AIExamResult>();

            foreach (var assignment in container.ClassAssignments)
            {
                var timeIndex = assignment.Slot.Index;
                var start = assignment.Slot.Start;
                var duration = assignment.Subject.Duration > 0 ? assignment.Subject.Duration : (int)(assignment.Slot.End - assignment.Slot.Start).TotalMinutes;
                var end = start.AddMinutes(duration);
                if (end > assignment.Slot.End)
                {
                    end = assignment.Slot.End;
                }

                var teacherList = teacherAssignments.TryGetValue((timeIndex, assignment.Room.RoomId), out var assignedTeachers)
                    ? assignedTeachers
                    : new List<int>();

                results.Add(new AIExamResult
                {
                    ModelSubjectId = assignment.Subject.SubjectId,
                    ModelRoomId = assignment.Room.RoomId,
                    ModelClassId = assignment.Class.Class.ModelClassId,
                    Duration = duration,
                    Date = assignment.Slot.Date,
                    StartTime = start.ToString("HH:mm"),
                    EndTime = end.ToString("HH:mm"),
                    StudentCount = assignment.Students,
                    SeatCount = assignment.Room.SeatCount,
                    TeacherList = teacherList.Select(id => new AIExamTeacherResult
                    {
                        ModelTeacherId = id
                    }).ToList()
                });
            }

            return results
                .OrderBy(r => r.Date)
                .ThenBy(r => r.StartTime)
                .ThenBy(r => r.ModelRoomId)
                .ThenBy(r => r.ModelClassId)
                .ToList();
        }

        #region 内部数据模型

        private sealed class TimeSlotInfo
        {
            public int Index { get; set; }
            public string Date { get; set; } = string.Empty;
            public string TimeNo { get; set; } = string.Empty;
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
        }

        private sealed class ClassInfo
        {
            public AIExamModelClass Class { get; set; } = null!;
            public int Grade { get; set; }
            public int StudentCount { get; set; }
        }

        private sealed class RoomInfo
        {
            public AIExamModelRoom Room { get; set; } = null!;
            public int RoomId { get; set; }
            public int BuildingId { get; set; }
            public string ExamMode { get; set; } = string.Empty;
            public int SeatCount { get; set; }
            public int TeacherCount { get; set; }
            public int? RoomNo { get; set; }
        }

        private sealed class TeacherInfo
        {
            public AIExamModelTeacher Teacher { get; set; } = null!;
            public int TeacherId { get; set; }
            public int Gender { get; set; }
        }

        private sealed class SubjectInfo
        {
            public AIExamModelSubject Subject { get; set; } = null!;
            public int SubjectId { get; set; }
            public string ExamMode { get; set; } = string.Empty;
            public int Duration { get; set; }
            public int Priority { get; set; }
            public List<ClassInfo> Classes { get; set; } = new();
        }

        private sealed class ClassRoomAssignment
        {
            public SubjectInfo Subject { get; set; } = null!;
            public ClassInfo Class { get; set; } = null!;
            public RoomInfo Room { get; set; } = null!;
            public TimeSlotInfo Slot { get; set; } = null!;
            public int Students { get; set; }
        }

        private sealed class ClassRoomShare
        {
            public ClassInfo Class { get; set; } = null!;
            public int Students { get; set; }
        }

        private sealed class RoomEvent
        {
            public RoomInfo Room { get; set; } = null!;
            public TimeSlotInfo Slot { get; set; } = null!;
            public SubjectInfo Subject { get; set; } = null!;
            public List<ClassRoomShare> ClassShares { get; } = new List<ClassRoomShare>();
            public int TotalStudents { get; set; }
            public DateTime StartTime => Slot.Start;
            public DateTime EndTime
            {
                get
                {
                    var duration = Subject.Duration > 0
                        ? Subject.Duration
                        : (int)(Slot.End - Slot.Start).TotalMinutes;
                    var end = Slot.Start.AddMinutes(duration);
                    return end <= Slot.End ? end : Slot.End;
                }
            }
        }

        private sealed class RoomCandidate
        {
            public RoomInfo Room { get; set; } = null!;
            public RoomEvent? ExistingEvent { get; set; }
            public int AvailableSeats => Math.Max(0, Room.SeatCount - (ExistingEvent?.TotalStudents ?? 0));
        }

        private sealed class RoomAllocation
        {
            public RoomInfo Room { get; set; } = null!;
            public int Students { get; set; }
            public RoomEvent? ExistingEvent { get; set; }
        }

        private sealed class RoomAllocationPlan
        {
            public List<RoomAllocation> Allocations { get; set; } = new();
            public int SeatWaste { get; set; }
            public int RoomCount { get; set; }
            public int MaxRoomCapacity { get; set; }
            public int RoomNoSpread { get; set; }
        }

        private sealed class RoomAssignmentContainer
        {
            public List<ClassRoomAssignment> ClassAssignments { get; } = new List<ClassRoomAssignment>();
            public List<RoomEvent> RoomEvents { get; } = new List<RoomEvent>();
            public Dictionary<(int timeIndex, int roomId), RoomEvent> EventLookup { get; set; } = new Dictionary<(int timeIndex, int roomId), RoomEvent>();
        }

        #endregion
    }

    #region 数据模型定义


    public class AIExamResult
    {
        public int ModelSubjectId { get; set; }
        public int ModelRoomId { get; set; }
        public int ModelClassId { get; set; }
        public int Duration { get; set; }
        public string? Date { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public int StudentCount { get; set; }
        public int SeatCount { get; set; }
        public List<AIExamTeacherResult> TeacherList { get; set; } = new List<AIExamTeacherResult>();
    }

    public class AIExamTeacherResult
    {
        public int ModelTeacherId { get; set; }
    }

    public class AIExamConfig
    {
        /// <summary>
        /// 最大学生每日考试次数
        /// </summary>
        public int MaxStudentDaily { get; set; } = 2;
        /// <summary>
        /// 最小考试间隔,单位分钟
        /// </summary>
        public int MinExamInterval { get; set; } = 10;
    }

    public class AIExamModel
    {
        /// <summary>
        /// 排考配置
        /// </summary>
        public AIExamConfig? Config { get; set; }
        /// <summary>
        /// 考试时间段列表
        /// </summary>
        public List<AIExamModelTime>? ModelTimeList { get; set; }
        /// <summary>
        /// 班级列表
        /// </summary>
        public List<AIExamModelClass>? ModelClassList { get; set; }
        /// <summary>
        /// 考场列表
        /// </summary>
        public List<AIExamModelRoom>? ModelRoomList { get; set; }
        /// <summary>
        /// 考试科目列表
        /// </summary>
        public List<AIExamModelSubject>? ModelSubjectList { get; set; }
        /// <summary>
        /// 监考教师列表
        /// </summary>
        public List<AIExamModelTeacher>? ModelTeacherList { get; set; }
        /// <summary>
        /// 同时考试的科目
        /// </summary>
        public List<AIExamRuleJointSubject>? RuleJointSubjectList { get; set; }
        /// <summary>
        /// 不能同时考试的科目
        /// </summary>
        public List<AIExamRuleJointSubjectNot>? RuleJointSubjectNotList { get; set; }
        /// <summary>
        /// 考试科目预分配考场
        /// </summary>
        public List<AIExamRuleRoomSubject>? RuleRoomSubjectList { get; set; }
        /// <summary>
        /// 考试科目禁止分配考场
        /// </summary>
        public List<AIExamRuleRoomSubject>? RuleRoomSubjectNotList { get; set; }
        /// <summary>
        /// 教师预监考教学楼
        /// </summary>
        public List<AIExamRuleTeacherBuilding>? RuleTeacherBuildingList { get; set; }
        /// <summary>
        /// 教师禁止预监考教学楼
        /// </summary>
        public List<AIExamRuleTeacherBuilding>? RuleTeacherBuildingNotList { get; set; }
        /// <summary>
        /// 教师预监考班级
        /// </summary>
        public List<AIExamRuleTeacherClass>? RuleTeacherClassList { get; set; }
        /// <summary>
        /// 教师禁止预监考班级
        /// </summary>
        public List<AIExamRuleTeacherClass>? RuleTeacherClassNotList { get; set; }
        /// <summary>
        /// 教师预监考考试科目
        /// </summary>
        public List<AIExamRuleTeacherSubject>? RuleTeacherSubjectList { get; set; }
        /// <summary>
        /// 教师禁止监考考试科目
        /// </summary>
        public List<AIExamRuleTeacherSubject>? RuleTeacherSubjectNotList { get; set; }
        /// <summary>
        /// 教师不可监考时间段
        /// </summary>
        public List<AIExamRuleTeacherUnTime>? RuleTeacherUnTimeList { get; set; }
    }

    public class AIExamModelTime
    {

        /// <summary>
        /// 日期
        /// </summary>
        public string? Date { get; set; }
        /// <summary>
        /// 考试场次,值是：上午场、下午场
        /// </summary>
        public string? TimeNo { get; set; }
        /// <summary>
        /// 考试场次开始时间
        /// </summary>
        public string? StartTime { get; set; }
        /// <summary>
        /// 考试场次结束时间
        /// </summary>
        public string? EndTime { get; set; }
    }

    public class AIExamModelClass
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
        /// 学生人数
        /// </summary>
        public int StudentCount { get; set; }
    }

    public class AIExamModelRoom
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
        /// 考试模式
        /// </summary>
        public string? ExamMode { get; set; }
        /// <summary>
        /// 优先级
        /// </summary>
        public int Priority { get; set; } = 5;
        /// <summary>
        /// 考场编号
        /// </summary>
        public int? RoomNo { get; set; }
        /// <summary>
        /// 座位数
        /// </summary>
        public int SeatCount { get; set; }
        /// <summary>
        /// 监考教师人数
        /// </summary>
        public int TeacherCount { get; set; }
    }

    public class AIExamModelSubject
    {
        /// <summary>
        /// 考试科目id
        /// </summary>
        public int ModelSubjectId { get; set; }
        /// <summary>
        /// 考试科目名称
        /// </summary>
        public string? ModelSubjectName { get; set; }
        /// <summary>
        /// 考试模式
        /// </summary>
        public string? ExamMode { get; set; }
        /// <summary>
        /// 预设考试日期
        /// </summary>
        public string? Date { get; set; }
        /// <summary>
        /// 预设考试开始时间
        /// </summary>
        public string? StartTime { get; set; }
        /// <summary>
        /// 考试时长
        /// </summary>
        public int Duration { get; set; }
        /// <summary>
        /// 考试难度
        /// </summary>
        public int Difficulty { get; set; }
        /// <summary>
        /// 优先级
        /// </summary>
        public int Priority { get; set; } = 1;
        /// <summary>
        /// 考试科目对应的班级列表
        /// </summary>
        public List<AIExamModelSubjectClass>? ModelSubjectClassList { get; set; }
    }

    public class AIExamModelSubjectClass
    {
        /// <summary>
        /// 班级id
        /// </summary>
        public int ModelClassId { get; set; }
    }

    public class AIExamModelTeacher
    {
        /// <summary>
        /// 教师id
        /// </summary>
        public int ModelTeacherId { get; set; }
        /// <summary>
        /// 教师名称
        /// </summary>
        public string? ModelTeacherName { get; set; }
        /// <summary>
        /// 性别
        /// </summary>
        public int Gender { get; set; }
    }

    public class AIExamRuleJointSubject
    {
        /// <summary>
        /// 预设考试日期
        /// </summary>
        public string? Date { get; set; }
        /// <summary>
        /// 预设考试开始时间
        /// </summary>
        public string? StartTime { get; set; }
        /// <summary>
        /// 同时考试的科目列表
        /// </summary>
        public List<AIExamRuleJointSubjectItem>? RuleJointSubjectList { get; set; }
    }

    public class AIExamRuleJointSubjectItem
    {
        /// <summary>
        /// 考试科目id
        /// </summary>
        public int ModelSubjectId { get; set; }
    }

    public class AIExamRuleJointSubjectNot
    {
        /// <summary>
        /// 不同考试的科目列表
        /// </summary>
        public List<AIExamRuleJointSubjectItem>? RuleJointSubjectList { get; set; }
    }

    public class AIExamRuleRoomSubject
    {
        /// <summary>
        /// 考试科目id
        /// </summary>
        public int ModelSubjectId { get; set; }
        /// <summary>
        /// 考场id
        /// </summary>
        public int ModelRoomId { get; set; }
    }
    public interface ITeacherRule
    {
        int TeacherId { get; }

        int TargetId { get; }
    }
    public class AIExamRuleTeacherBuilding : ITeacherRule
    {
        public int ModelTeacherId { get; set; }
        public int ModelBuildingId { get; set; }
        public int TeacherId => ModelTeacherId;
        public int TargetId => ModelBuildingId;
    }

    public class AIExamRuleTeacherClass : ITeacherRule
    {
        /// <summary>
        ///        
        /// </summary>
        public int ModelTeacherId { get; set; }
        public int ModelClassId { get; set; }
        public int TeacherId => ModelTeacherId;
        public int TargetId => ModelClassId;
    }

    public class AIExamRuleTeacherSubject : ITeacherRule
    {
        public int ModelTeacherId { get; set; }
        public int ModelSubjectId { get; set; }
        public int TeacherId => ModelTeacherId;
        public int TargetId => ModelSubjectId;
    }

    public class AIExamRuleTeacherUnTime
    {
        /// <summary>
        /// 教师id
        /// </summary>
        public int ModelTeacherId { get; set; }

        /// <summary>
        /// 不可监考时间开始时间
        /// </summary>
        public string? StartTime { get; set; }
        /// <summary>
        /// 不可监考时间结束时间
        /// </summary>
        public string? EndTime { get; set; }
    }

    #endregion
}