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
        // 0 表示不限制候选考场数量，避免由于过早裁剪导致大班级无法满足同楼层多考场的需求。
        private const int MaxRoomCandidatesPerClass = 0;

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

            var subjectTimeStates = InitializeSubjectTimeStates(subjects, timeSlots, model, config, error);
            if (error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var conflictCuts = new List<SlotTimeConflict>();
            var conflictCutKeys = new HashSet<string>();
            RoomAssignmentContainer? roomAssignments = null;
            Dictionary<int, int>? subjectTimeAssignments = null;
            SlotTimeConflict? lastConflict = null;
            var persistentClassPreferences = new Dictionary<int, ClassRoomPreference>();
            var failureReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var maxIterations = (int)Math.Max(1, Math.Min(50L, Math.Max(1L, (long)Math.Max(1, subjects.Count) * Math.Max(1, timeSlots.Count))));
            var attempt = 0;
            var needTimeSolve = true;

            while (attempt < maxIterations)
            {
                attempt++;

                if (needTimeSolve || subjectTimeAssignments == null)
                {
                    subjectTimeAssignments = SolveSubjectTimeAllocation(config, subjects, timeSlots, model, conflictCuts, subjectTimeStates, error);
                    if (subjectTimeAssignments == null)
                    {
                        throw new ResponseException(error.ToString(), ErrorCode.ParamError);
                    }
                }

                var errorLengthBeforeRooms = error.Length;

                roomAssignments = AllocateRooms(subjects, rooms, timeSlots, subjectTimeAssignments, model, config, persistentClassPreferences, out var conflict, error);
                if (roomAssignments != null)
                {
                    break;
                }

                var appendedLength = error.Length - errorLengthBeforeRooms;
                if (appendedLength > 0)
                {
                    var appended = error.ToString(errorLengthBeforeRooms, appendedLength);
                    CollectFailureReasons(failureReasons, appended);
                }

                error.Length = errorLengthBeforeRooms;

                if (conflict == null)
                {
                    break;
                }

                if (TryExpandSubjectTimeWindow(conflict, subjectTimeStates, config))
                {
                    needTimeSolve = true;
                    lastConflict = conflict;
                    if (!TryAddConflictCut(conflictCuts, conflictCutKeys, conflict))
                    {
                        break;
                    }
                    continue;
                }

                if (RemoveClassPreferences(conflict, persistentClassPreferences))
                {
                    needTimeSolve = false;
                    continue;
                }

                needTimeSolve = true;
                lastConflict = conflict;

                if (!TryAddConflictCut(conflictCuts, conflictCutKeys, conflict))
                {
                    break;
                }
            }

            if (roomAssignments == null || subjectTimeAssignments == null)
            {
                if (lastConflict != null)
                {
                    var slot = timeSlots[lastConflict.TimeIndex];
                    var subjectNames = subjects
                        .Where(s => lastConflict.SubjectIds.Contains(s.SubjectId))
                        .Select(s => s.Subject.ModelSubjectName ?? s.SubjectId.ToString())
                        .ToList();
                    if (subjectNames.Count > 0)
                    {
                        error.AppendLine($"多次调整后仍无法在场次 {slot.Date} {slot.Start:HH:mm} 安排科目 {string.Join("、", subjectNames)} 的考场，请检查容量或规则设置。");
                    }
                }

                AppendFailureReasonSummary(failureReasons, config, error);
                error.AppendLine("多次调整考试时间后仍无法满足考场约束，请检查排考数据是否存在冲突。");
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var teacherAssignments = AssignTeachers(teachers, roomAssignments.RoomEvents, model, error);
            if (teacherAssignments == null)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            return BuildResults(roomAssignments, teacherAssignments);

        }

        private static bool TryAddConflictCut(List<SlotTimeConflict> conflictCuts, HashSet<string> conflictCutKeys, SlotTimeConflict conflict)
        {
            var key = BuildConflictCutKey(conflict);
            if (!conflictCutKeys.Add(key))
            {
                return false;
            }

            conflictCuts.Add(conflict);
            return true;
        }

        private static string BuildConflictCutKey(SlotTimeConflict conflict)
        {
            var orderedSubjects = conflict.SubjectIds != null && conflict.SubjectIds.Count > 0
                ? conflict.SubjectIds.OrderBy(id => id)
                : Enumerable.Empty<int>();
            return $"{conflict.TimeIndex}:{string.Join(',', orderedSubjects)}";
        }

        private static bool RemoveClassPreferences(SlotTimeConflict conflict, Dictionary<int, ClassRoomPreference> persistentClassPreferences)
        {
            if (conflict.ClassIds == null || conflict.ClassIds.Count == 0)
            {
                return false;
            }

            var removed = false;
            foreach (var classId in conflict.ClassIds)
            {
                if (persistentClassPreferences.Remove(classId))
                {
                    removed = true;
                }
            }

            return removed;
        }

        private static bool TryExpandSubjectTimeWindow(SlotTimeConflict conflict,
            Dictionary<int, SubjectTimeAdjustmentState> subjectTimeStates,
            AIExamConfig config)
        {
            if (conflict.SubjectIds == null || conflict.SubjectIds.Count == 0)
            {
                return false;
            }

            var expanded = false;
            foreach (var subjectId in conflict.SubjectIds)
            {
                if (!subjectTimeStates.TryGetValue(subjectId, out var state))
                {
                    continue;
                }

                if (TryExpandSubjectTolerance(state, config))
                {
                    expanded = true;
                }
            }

            return expanded;
        }

        private static void CollectFailureReasons(Dictionary<string, int> failureReasons, string appended)
        {
            if (string.IsNullOrWhiteSpace(appended))
            {
                return;
            }

            var lines = appended
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l));

            foreach (var line in lines)
            {
                if (failureReasons.TryGetValue(line, out var count))
                {
                    failureReasons[line] = count + 1;
                }
                else
                {
                    failureReasons[line] = 1;
                }
            }
        }

        private static void AppendFailureReasonSummary(Dictionary<string, int> failureReasons, AIExamConfig config, StringBuilder error)
        {
            if (failureReasons.Count == 0)
            {
                return;
            }

            var topN = Math.Max(1, config.FailureReasonTopN);
            var summary = failureReasons
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Take(topN)
                .ToList();

            if (summary.Count == 0)
            {
                return;
            }

            error.AppendLine("排考失败原因统计：");
            foreach (var item in summary)
            {
                error.AppendLine($"{item.Value} 次 - {item.Key}");
            }
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
            var order = 0;
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
                    StudentCount = item.StudentCount,
                    Order = order++
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

                var orderedClassList = classList
                    .OrderBy(c => c.Order)
                    .ToList();

                result.Add(new SubjectInfo
                {
                    Subject = subject,
                    SubjectId = subject.ModelSubjectId,
                    ExamMode = subject.ExamMode ?? string.Empty,
                    Duration = Math.Max(subject.Duration, 0),
                    Priority = subject.Priority,
                    Classes = orderedClassList
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

        private static Dictionary<int, SubjectTimeAdjustmentState> InitializeSubjectTimeStates(
            List<SubjectInfo> subjects,
            List<TimeSlotInfo> timeSlots,
            AIExamModel model,
            AIExamConfig config,
            StringBuilder error)
        {
            var result = new Dictionary<int, SubjectTimeAdjustmentState>();
            var slotLookup = timeSlots.ToDictionary(t => t.Index);

            foreach (var subject in subjects)
            {
                var state = new SubjectTimeAdjustmentState
                {
                    Subject = subject,
                    RequiredDuration = subject.Duration > 0 ? subject.Duration : 60,
                    MaxToleranceMinutes = Math.Max(0, config.TimeAdjustWindowMinutes)
                };

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

                state.PreferredDate = specifiedDate;

                if (!string.IsNullOrWhiteSpace(specifiedDate) && !string.IsNullOrWhiteSpace(specifiedStart) && TryParseDateTime(specifiedDate, specifiedStart, out var preferredStart))
                {
                    state.PreferredStart = preferredStart;
                }

                foreach (var slot in timeSlots)
                {
                    if (!string.IsNullOrWhiteSpace(specifiedDate) && !slot.Date.Equals(specifiedDate, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var duration = (int)Math.Round((slot.End - slot.Start).TotalMinutes);
                    if (duration < state.RequiredDuration)
                    {
                        continue;
                    }

                    state.OrderedCandidates.Add(slot.Index);

                    if (state.PreferredStart.HasValue)
                    {
                        state.CandidateDistance[slot.Index] = (int)Math.Abs((slot.Start - state.PreferredStart.Value).TotalMinutes);
                    }
                }

                if (state.OrderedCandidates.Count == 0)
                {
                    if (!string.IsNullOrWhiteSpace(specifiedDate))
                    {
                        error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 在指定日期 {specifiedDate} 内没有合适的考试场次。");
                    }
                    else
                    {
                        error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 没有可用的考试时间段。");
                    }
                    continue;
                }

                state.OrderedCandidates.Sort((a, b) =>
                {
                    var diffA = state.CandidateDistance.TryGetValue(a, out var da) ? da : 0;
                    var diffB = state.CandidateDistance.TryGetValue(b, out var db) ? db : 0;
                    var compare = diffA.CompareTo(diffB);
                    if (compare != 0)
                    {
                        return compare;
                    }

                    var slotA = slotLookup[a];
                    var slotB = slotLookup[b];
                    compare = slotA.Start.CompareTo(slotB.Start);
                    if (compare != 0)
                    {
                        return compare;
                    }

                    return a.CompareTo(b);
                });

                if (state.PreferredStart == null)
                {
                    state.CurrentToleranceMinutes = state.MaxToleranceMinutes;
                }

                RefreshActiveCandidates(state);

                if (state.ActiveCandidates.Count == 0)
                {
                    if (!EnsureMinimumCandidates(state, config))
                    {
                        if (!string.IsNullOrWhiteSpace(specifiedDate))
                        {
                            error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 在允许的调整范围内没有可用的考试场次。");
                        }
                        else
                        {
                            error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 没有可用的考试时间段。");
                        }
                        continue;
                    }
                }

                result[subject.SubjectId] = state;
            }

            return result;
        }

        private static Dictionary<int, int>? SolveSubjectTimeAllocation(AIExamConfig config,
            List<SubjectInfo> subjects,
            List<TimeSlotInfo> timeSlots,
            AIExamModel model,
            List<SlotTimeConflict> conflictCuts,
            Dictionary<int, SubjectTimeAdjustmentState> subjectTimeStates,
            StringBuilder error)
        {
            var cpModel = new CpModel();
            var subjectTimeVars = new Dictionary<(int subjectId, int timeIndex), BoolVar>();
            var subjectCandidates = new Dictionary<int, List<int>>();

            foreach (var subject in subjects)
            {
                if (!subjectTimeStates.TryGetValue(subject.SubjectId, out var state))
                {
                    error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 没有可用的考试时间段。");
                    return null;
                }

                if (state.ActiveCandidates.Count == 0 && !EnsureMinimumCandidates(state, config))
                {
                    error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 在允许的调整范围内没有可用的考试场次。");
                    return null;
                }

                var candidates = state.ActiveCandidates.ToList();
                if (candidates.Count == 0)
                {
                    error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 没有可用的考试时间段。");
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

            foreach (var conflict in conflictCuts)
            {
                if (conflict.SubjectIds.Count == 0)
                {
                    continue;
                }

                var varsInConflict = new List<BoolVar>();
                foreach (var subjectId in conflict.SubjectIds)
                {
                    if (!subjectCandidates.TryGetValue(subjectId, out var timeList) || !timeList.Contains(conflict.TimeIndex))
                    {
                        continue;
                    }

                    if (subjectTimeVars.TryGetValue((subjectId, conflict.TimeIndex), out var variable))
                    {
                        varsInConflict.Add(variable);
                    }
                }

                if (varsInConflict.Count == 0)
                {
                    continue;
                }

                if (varsInConflict.Count == 1)
                {
                    cpModel.Add(varsInConflict[0] == 0);
                }
                else
                {
                    cpModel.Add(LinearExpr.Sum(varsInConflict) <= varsInConflict.Count - 1);
                }
            }

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

        private static bool EnsureMinimumCandidates(SubjectTimeAdjustmentState state, AIExamConfig config)
        {
            if (state.ActiveCandidates.Count > 0)
            {
                return true;
            }

            if (state.OrderedCandidates.Count == 0)
            {
                return false;
            }

            if (state.PreferredStart == null)
            {
                RefreshActiveCandidates(state);
                return state.ActiveCandidates.Count > 0;
            }

            if (state.MaxToleranceMinutes <= 0 || config.TimeAdjustStepMinutes <= 0)
            {
                return false;
            }

            while (TryExpandSubjectTolerance(state, config))
            {
                if (state.ActiveCandidates.Count > 0)
                {
                    return true;
                }
            }

            return state.ActiveCandidates.Count > 0;
        }

        private static bool TryExpandSubjectTolerance(SubjectTimeAdjustmentState state, AIExamConfig config)
        {
            if (state.PreferredStart == null)
            {
                return false;
            }

            if (state.MaxToleranceMinutes <= 0)
            {
                return false;
            }

            if (config.TimeAdjustStepMinutes <= 0)
            {
                return false;
            }

            if (state.CurrentToleranceMinutes >= state.MaxToleranceMinutes)
            {
                return false;
            }

            var step = Math.Max(1, config.TimeAdjustStepMinutes);
            var newTolerance = Math.Min(state.MaxToleranceMinutes, state.CurrentToleranceMinutes + step);
            if (newTolerance == state.CurrentToleranceMinutes)
            {
                return false;
            }

            state.CurrentToleranceMinutes = newTolerance;
            RefreshActiveCandidates(state);
            return true;
        }

        private static void RefreshActiveCandidates(SubjectTimeAdjustmentState state)
        {
            state.ActiveCandidates.Clear();

            if (state.OrderedCandidates.Count == 0)
            {
                return;
            }

            if (state.PreferredStart == null)
            {
                state.ActiveCandidates.AddRange(state.OrderedCandidates);
                return;
            }

            if (state.MaxToleranceMinutes <= 0)
            {
                foreach (var index in state.OrderedCandidates)
                {
                    if (state.CandidateDistance.TryGetValue(index, out var diff) && diff == 0)
                    {
                        state.ActiveCandidates.Add(index);
                    }
                }
                return;
            }

            foreach (var index in state.OrderedCandidates)
            {
                if (state.CandidateDistance.TryGetValue(index, out var diff) && diff <= state.CurrentToleranceMinutes)
                {
                    state.ActiveCandidates.Add(index);
                }
            }
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

                foreach (var grouping in vars.Where(v => subjectIds.Contains(v.Key.subjectId)).GroupBy(v => v.Key.timeIndex))
                {
                    var timeIndex = grouping.Key;
                    var list = grouping.Select(v => v.Value).ToList();
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
            AIExamConfig config,
            Dictionary<int, ClassRoomPreference> persistentClassPreferences,
            out SlotTimeConflict? conflict,
            StringBuilder error)
        {
            conflict = null;
            var roomSubjectAllow = BuildSubjectRoomRule(model.RuleRoomSubjectList);
            var roomSubjectBlock = BuildSubjectRoomRule(model.RuleRoomSubjectNotList);

            var container = new RoomAssignmentContainer();
            var eventsLookup = new Dictionary<(int timeIndex, int roomId), List<RoomEvent>>();
            container.EventLookup = eventsLookup;
            var classPreferences = persistentClassPreferences
                .ToDictionary(kvp => kvp.Key, kvp => ClonePreference(kvp.Value));

            var subjectWithTime = subjects
                .Where(s => subjectTimeAssignments.ContainsKey(s.SubjectId))
                .Select(s => new
                {
                    Subject = s,
                    TimeIndex = subjectTimeAssignments[s.SubjectId]
                })
                .OrderBy(s => timeSlots[s.TimeIndex].Start)
                .ThenBy(s => s.Subject.Priority)
                .ThenByDescending(s => s.Subject.Duration)
                .ToList();

            foreach (var group in subjectWithTime.GroupBy(s => s.TimeIndex).OrderBy(g => g.Key))
            {
                var timeIndex = group.Key;
                var slot = timeSlots[timeIndex];
                var slotSubjects = group.Select(g => g.Subject)
                    .OrderBy(s => s.Priority)
                    .ThenByDescending(s => s.Duration)
                    .ThenByDescending(s => s.Classes.Sum(c => c.StudentCount))
                    .ToList();

                var slotRooms = rooms.Values
                    .Select(room => new RoomCandidate
                    {
                        Room = room
                    })
                    .Where(candidate => candidate.AvailableSeats > 0)
                    .OrderBy(candidate => candidate.Room.SeatCount)
                    .ThenBy(candidate => candidate.Room.RoomNo ?? int.MaxValue)
                    .ThenBy(candidate => candidate.Room.RoomId)
                    .ToList();

                if (slotRooms.Count == 0)
                {
                    error.AppendLine($"场次 {slot.Date} {slot.Start:HH:mm} 没有可用考场容量。");
                    return null;
                }

                var slotClassRequests = new List<SlotClassRequest>();
                var usedRoomIndices = new HashSet<int>();

                foreach (var subject in slotSubjects)
                {
                    if (!subjectTimeAssignments.TryGetValue(subject.SubjectId, out _))
                    {
                        error.AppendLine($"缺少科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 的考试时间安排。");
                        return null;
                    }

                    var orderedClasses = subject.Classes
                        .OrderBy(c => c.Order)
                        .ThenBy(c => c.Grade)
                        .ThenByDescending(c => c.StudentCount)
                        .ToList();

                    foreach (var cls in orderedClasses)
                    {
                        var request = new SlotClassRequest
                        {
                            Subject = subject,
                            Class = cls
                        };

                        var candidateIndices = new List<int>();

                        for (var roomIndex = 0; roomIndex < slotRooms.Count; roomIndex++)
                        {
                            var candidate = slotRooms[roomIndex];

                            if (!string.IsNullOrWhiteSpace(subject.ExamMode) && !string.Equals(candidate.Room.ExamMode, subject.ExamMode, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (roomSubjectBlock.TryGetValue(subject.SubjectId, out var blocked) && blocked.Contains(candidate.Room.RoomId))
                            {
                                continue;
                            }

                            if (roomSubjectAllow.TryGetValue(subject.SubjectId, out var allowed) && !allowed.Contains(candidate.Room.RoomId))
                            {
                                continue;
                            }

                            candidateIndices.Add(roomIndex);
                        }

                        if (candidateIndices.Count == 0)
                        {
                            error.AppendLine($"科目 {subject.Subject.ModelSubjectName ?? subject.SubjectId.ToString()} 的班级 {cls.Class.ModelClassName ?? cls.Class.ModelClassId.ToString()} 在 {slot.Date} {slot.Start:HH:mm} 没有可用的考场。");
                            return null;
                        }

                        if (MaxRoomCandidatesPerClass > 0 && candidateIndices.Count > MaxRoomCandidatesPerClass)
                        {
                            var originalCandidates = candidateIndices.ToList();
                            var preferredSet = new HashSet<int>();
                            if (classPreferences.TryGetValue(cls.Class.ModelClassId, out var existingPreference))
                            {
                                foreach (var preferredRoomId in existingPreference.RoomIds)
                                {
                                    var index = originalCandidates.FindIndex(idx => slotRooms[idx].Room.RoomId == preferredRoomId);
                                    if (index >= 0)
                                    {
                                        preferredSet.Add(originalCandidates[index]);
                                    }
                                }
                            }

                            foreach (var preferredIndex in request.PreferredRooms)
                            {
                                if (preferredIndex >= 0 && preferredIndex < slotRooms.Count && originalCandidates.Contains(preferredIndex))
                                {
                                    preferredSet.Add(preferredIndex);
                                }
                            }

                            var ordered = originalCandidates
                                .OrderBy(idx => Math.Max(0, slotRooms[idx].AvailableSeats - cls.StudentCount))
                                .ThenBy(idx => slotRooms[idx].Room.SeatCount)
                                .ThenBy(idx => slotRooms[idx].Room.RoomNo ?? int.MaxValue)
                                .ThenBy(idx => slotRooms[idx].Room.RoomId)
                                .ToList();

                            var limitedList = ordered.Take(MaxRoomCandidatesPerClass).ToList();
                            var unionSet = new HashSet<int>(limitedList);
                            foreach (var preferred in preferredSet)
                            {
                                unionSet.Add(preferred);
                            }

                            candidateIndices = originalCandidates.Where(unionSet.Contains).ToList();

                            if (candidateIndices.Count == 0)
                            {
                                candidateIndices = limitedList.Count > 0 ? limitedList : originalCandidates;
                            }
                        }

                        var classId = cls.Class.ModelClassId;

                        if (classPreferences.TryGetValue(classId, out var preference))
                        {
                            var preferredRoomSet = preference.RoomIds.ToHashSet();
                            var preferredCandidates = candidateIndices
                                .Where(idx => slotRooms[idx].Room.BuildingId == preference.BuildingId && preferredRoomSet.Contains(slotRooms[idx].Room.RoomId))
                                .OrderBy(idx => preference.RoomIds.IndexOf(slotRooms[idx].Room.RoomId))
                                .ToList();

                            if (preferredCandidates.Count > 0)
                            {
                                request.PreferredRooms = preferredCandidates;
                            }
                        }

                        request.CandidateRooms = candidateIndices;

                        foreach (var idx in candidateIndices)
                        {
                            usedRoomIndices.Add(idx);
                        }

                        slotClassRequests.Add(request);
                    }
                }

                if (usedRoomIndices.Count == 0)
                {
                    error.AppendLine($"场次 {slot.Date} {slot.Start:HH:mm} 没有满足条件的考场可用。");
                    return null;
                }

                var indexMap = new Dictionary<int, int>();
                var filteredRooms = new List<RoomCandidate>();
                for (var originalIndex = 0; originalIndex < slotRooms.Count; originalIndex++)
                {
                    if (!usedRoomIndices.Contains(originalIndex))
                    {
                        continue;
                    }

                    indexMap[originalIndex] = filteredRooms.Count;
                    filteredRooms.Add(slotRooms[originalIndex]);
                }

                foreach (var request in slotClassRequests)
                {
                    for (var i = 0; i < request.CandidateRooms.Count; i++)
                    {
                        request.CandidateRooms[i] = indexMap[request.CandidateRooms[i]];
                    }

                    if (request.PreferredRooms.Count > 0)
                    {
                        for (var i = 0; i < request.PreferredRooms.Count; i++)
                        {
                            request.PreferredRooms[i] = indexMap[request.PreferredRooms[i]];
                        }
                    }
                }

                var baseRequests = slotClassRequests
                    .Select(CloneSlotClassRequest)
                    .ToList();

                SlotRoomAllocationResult? allocationResult = null;
                var usedLevel = RoomRelaxationLevel.Full;
                var relaxationLevels = new[]
                {
                    RoomRelaxationLevel.StrictPreferred,
                    RoomRelaxationLevel.PreferredBuilding,
                    RoomRelaxationLevel.Full
                };

                foreach (var level in relaxationLevels)
                {
                    var stagedRequests = baseRequests.Select(CloneSlotClassRequest).ToList();
                    if (!ApplyRoomRelaxation(level, stagedRequests, filteredRooms, classPreferences))
                    {
                        continue;
                    }

                    allocationResult = SolveSlotRoomAllocationWithCp(
                        stagedRequests,
                        filteredRooms,
                        classPreferences,
                        slot,
                        config,
                        slotSubjects,
                        model);
                    if (allocationResult != null)
                    {
                        slotClassRequests = stagedRequests;
                        usedLevel = level;
                        break;
                    }
                }

                if (allocationResult == null)
                {
                    var impactedClasses = slotClassRequests
                        .Where(r => classPreferences.ContainsKey(r.Class.Class.ModelClassId) || r.PreferredRooms.Count > 0)
                        .Select(r => r.Class.Class.ModelClassId)
                        .Distinct()
                        .ToList();

                    if (impactedClasses.Count == 0)
                    {
                        impactedClasses = slotClassRequests
                            .Select(r => r.Class.Class.ModelClassId)
                            .Distinct()
                            .ToList();
                    }

                    conflict = new SlotTimeConflict
                    {
                        TimeIndex = timeIndex,
                        SubjectIds = slotSubjects.Select(s => s.Subject.ModelSubjectId).Distinct().ToList(),
                        ClassIds = impactedClasses
                    };
                    return null;
                }

                var subjectOffsets = allocationResult.SubjectStartOffsets;
                if (subjectOffsets.Count == 0)
                {
                    var fallbackOffsets = ComputeSubjectStartOffsets(slotSubjects, slot, allocationResult, config, model);
                    if (fallbackOffsets == null)
                    {
                        error.AppendLine($"无法在场次 {slot.Date} {slot.Start:HH:mm} 为科目的考试安排合适的开始时间。");
                        return null;
                    }

                    subjectOffsets = fallbackOffsets;
                    allocationResult.SubjectStartOffsets.Clear();
                    foreach (var kv in fallbackOffsets)
                    {
                        allocationResult.SubjectStartOffsets[kv.Key] = kv.Value;
                    }
                }

                if (!ValidateRoomSchedules(slotSubjects, allocationResult, slot, subjectOffsets, config, out var scheduleConflict))
                {
                    var subjectIdSet = new HashSet<int>(slotSubjects.Select(s => s.SubjectId));
                    conflict = scheduleConflict ?? new SlotTimeConflict
                    {
                        TimeIndex = timeIndex,
                        SubjectIds = subjectIdSet.ToList(),
                        ClassIds = allocationResult.ClassAllocations
                            .Where(kvp => subjectIdSet.Contains(kvp.Key.subjectId))
                            .Select(kvp => kvp.Key.classId)
                            .Distinct()
                            .ToList()
                    };

                    return null;
                }

                foreach (var request in slotClassRequests)
                {
                    var classId = request.Class.Class.ModelClassId;
                    var subjectId = request.Subject.SubjectId;

                    if (!allocationResult.ClassAllocations.TryGetValue((subjectId, classId), out var allocations) || allocations.Count == 0)
                    {
                        error.AppendLine($"无法为科目 {request.Subject.Subject.ModelSubjectName ?? subjectId.ToString()} 的班级 {request.Class.Class.ModelClassName ?? classId.ToString()} 分配考场。");
                        return null;
                    }

                    var buildingId = allocationResult.ClassBuilding.TryGetValue(classId, out var value)
                        ? value
                        : allocations.First().Room.BuildingId;

                    var hadPreference = classPreferences.ContainsKey(classId);
                    if (!hadPreference || usedLevel != RoomRelaxationLevel.StrictPreferred)
                    {
                        classPreferences[classId] = new ClassRoomPreference
                        {
                            BuildingId = buildingId,
                            RoomIds = allocations
                                .OrderBy(a => a.Room.RoomNo.HasValue ? 0 : 1)
                                .ThenBy(a => a.Room.RoomNo ?? a.Room.RoomId)
                                .Select(a => a.Room.RoomId)
                                .ToList()
                        };
                    }

                    var assignedStudents = 0;

                    foreach (var allocation in allocations)
                    {
                        var key = (slot.Index, allocation.Room.RoomId);
                        if (!eventsLookup.TryGetValue(key, out var roomEvents))
                        {
                            roomEvents = new List<RoomEvent>();
                            eventsLookup[key] = roomEvents;
                        }

                        var subjectDuration = GetSubjectDurationForSlot(request.Subject, slot);
                        if (subjectDuration <= 0)
                        {
                            error.AppendLine($"科目 {request.Subject.Subject.ModelSubjectName ?? subjectId.ToString()} 的考试时长无效。");
                            return null;
                        }

                        var startOffset = subjectOffsets.TryGetValue(subjectId, out var offsetValue) ? offsetValue : 0;
                        var eventStart = slot.Start.AddMinutes(startOffset);
                        var eventEnd = eventStart.AddMinutes(subjectDuration);
                        if (eventEnd > slot.End)
                        {
                            eventEnd = slot.End;
                        }

                        if (eventStart < slot.Start || eventEnd > slot.End)
                        {
                            error.AppendLine($"科目 {request.Subject.Subject.ModelSubjectName ?? subjectId.ToString()} 在场次 {slot.Date} {slot.Start:HH:mm} 的考试时间超出场次范围。");
                            return null;
                        }

                        foreach (var existingEvent in roomEvents.Where(e => e.Subject.SubjectId != subjectId))
                        {
                            if (!HasSufficientGap(existingEvent.StartTime, existingEvent.EndTime, eventStart, eventEnd, config.MinExamInterval))
                            {
                                var existingName = existingEvent.Subject.Subject.ModelSubjectName ?? existingEvent.Subject.SubjectId.ToString();
                                error.AppendLine($"考场 {allocation.Room.Room.ModelRoomName ?? allocation.Room.RoomId.ToString()} 在 {slot.Date} {slot.Start:HH:mm} 已安排科目 {existingName}，无法在最小间隔内再安排科目 {request.Subject.Subject.ModelSubjectName ?? subjectId.ToString()}。");
                                return null;
                            }
                        }

                        var roomEvent = roomEvents.FirstOrDefault(e => e.Subject.SubjectId == request.Subject.SubjectId);

                        if (roomEvent == null)
                        {
                            var usageLimit = GetRoomUsageLimit(slot.TimeNo);
                            if (usageLimit > 0 && roomEvents.Count >= usageLimit)
                            {
                                error.AppendLine($"考场 {allocation.Room.Room.ModelRoomName ?? allocation.Room.RoomId.ToString()} 在 {slot.Date} {slot.TimeNo} 的考试次数已达到上限 {usageLimit} 次，无法继续安排科目 {request.Subject.Subject.ModelSubjectName ?? subjectId.ToString()}。");
                                return null;
                            }
                        }

                        if (roomEvent == null)
                        {
                            foreach (var existingGlobalEvent in container.RoomEvents.Where(e => e.Room.RoomId == allocation.Room.RoomId))
                            {
                                if (existingGlobalEvent.Slot.Index == slot.Index && existingGlobalEvent.Subject.SubjectId == subjectId)
                                {
                                    continue;
                                }

                                var existingDuration = GetSubjectDurationForSlot(existingGlobalEvent.Subject, existingGlobalEvent.Slot);
                                if (existingDuration <= 0)
                                {
                                    continue;
                                }

                                var existingStart = existingGlobalEvent.Slot.Start.AddMinutes(existingGlobalEvent.StartOffsetMinutes);
                                var existingEnd = existingStart.AddMinutes(existingDuration);
                                if (existingEnd > existingGlobalEvent.Slot.End)
                                {
                                    existingEnd = existingGlobalEvent.Slot.End;
                                }

                                if (!HasSufficientGap(existingStart, existingEnd, eventStart, eventEnd, config.MinExamInterval))
                                {
                                    var conflictSubjects = new HashSet<int> { subjectId, existingGlobalEvent.Subject.SubjectId };
                                    var conflictClassIds = new HashSet<int> { request.Class.Class.ModelClassId };
                                    conflictClassIds.UnionWith(existingGlobalEvent.ClassShares.Select(share => share.Class.Class.ModelClassId));

                                    conflict = new SlotTimeConflict
                                    {
                                        TimeIndex = slot.Index,
                                        SubjectIds = conflictSubjects.ToList(),
                                        ClassIds = conflictClassIds.ToList()
                                    };

                                    return null;
                                }
                            }
                        }

                        if (roomEvent == null)
                        {
                            roomEvent = new RoomEvent
                            {
                                Room = allocation.Room,
                                Slot = slot,
                                Subject = request.Subject,
                                StartOffsetMinutes = startOffset
                            };
                            roomEvents.Add(roomEvent);
                            container.RoomEvents.Add(roomEvent);
                        }
                        else
                        {
                            roomEvent.StartOffsetMinutes = startOffset;
                        }

                        var existingShare = roomEvent.ClassShares.FirstOrDefault(s => s.Class.Class.ModelClassId == classId);
                        if (existingShare == null)
                        {
                            existingShare = new ClassRoomShare
                            {
                                Class = request.Class,
                                Students = allocation.Students
                            };
                            roomEvent.ClassShares.Add(existingShare);
                        }
                        else
                        {
                            existingShare.Students += allocation.Students;
                        }

                        roomEvent.TotalStudents += allocation.Students;

                        if (!ValidateRoomGradeSharing(roomEvent, error))
                        {
                            return null;
                        }

                        if (roomEvent.TotalStudents > roomEvent.Room.SeatCount)
                        {
                            error.AppendLine($"考场 {roomEvent.Room.Room.ModelRoomName ?? roomEvent.Room.RoomId.ToString()} 的学生数量超过座位容量。");
                            return null;
                        }

                        if (allocation.Students > allocation.Room.SeatCount)
                        {
                            error.AppendLine($"考场 {allocation.Room.Room.ModelRoomName ?? allocation.Room.RoomId.ToString()} 分配的学生数量超过了座位容量。");
                            return null;
                        }

                        container.ClassAssignments.Add(new ClassRoomAssignment
                        {
                            Subject = request.Subject,
                            Class = request.Class,
                            Room = allocation.Room,
                            Slot = slot,
                            Students = allocation.Students,
                            StartOffsetMinutes = startOffset
                        });

                        assignedStudents += allocation.Students;
                    }

                    if (assignedStudents != request.Class.StudentCount)
                    {
                        error.AppendLine($"班级 {request.Class.Class.ModelClassName ?? classId.ToString()} 未完全分配（当前 {assignedStudents}/{request.Class.StudentCount}）。");
                        return null;
                    }
                }
            }

            persistentClassPreferences.Clear();
            foreach (var kvp in classPreferences)
            {
                persistentClassPreferences[kvp.Key] = ClonePreference(kvp.Value);
            }

            return container;
        }

        private enum RoomRelaxationLevel
        {
            StrictPreferred,
            PreferredBuilding,
            Full
        }

        private static SlotClassRequest CloneSlotClassRequest(SlotClassRequest request)
        {
            return new SlotClassRequest
            {
                Subject = request.Subject,
                Class = request.Class,
                CandidateRooms = new List<int>(request.CandidateRooms),
                PreferredRooms = new List<int>(request.PreferredRooms)
            };
        }

        private static ClassRoomPreference ClonePreference(ClassRoomPreference preference)
        {
            return new ClassRoomPreference
            {
                BuildingId = preference.BuildingId,
                RoomIds = preference.RoomIds != null
                    ? new List<int>(preference.RoomIds)
                    : new List<int>()
            };
        }

        private static bool ValidateRoomGradeSharing(RoomEvent roomEvent, StringBuilder error)
        {
            var gradeSet = new HashSet<int>();
            foreach (var share in roomEvent.ClassShares)
            {
                var grade = share.Class.Class.Grade;
                if (!gradeSet.Add(grade))
                {
                    error.AppendLine($"考场 {roomEvent.Room.Room.ModelRoomName ?? roomEvent.Room.RoomId.ToString()} 在 {roomEvent.Slot.Date} {roomEvent.Slot.Start:HH:mm} 同时安排了相同年级的多个班级，违反排考规则。");
                    return false;
                }
            }

            if (gradeSet.Count > 2)
            {
                error.AppendLine($"考场 {roomEvent.Room.Room.ModelRoomName ?? roomEvent.Room.RoomId.ToString()} 在 {roomEvent.Slot.Date} {roomEvent.Slot.Start:HH:mm} 安排的班级超过两个年级，违反排考规则。");
                return false;
            }

            return true;
        }

        private static bool ApplyRoomRelaxation(RoomRelaxationLevel level,
            List<SlotClassRequest> requests,
            List<RoomCandidate> roomCandidates,
            Dictionary<int, ClassRoomPreference> classPreferences)
        {
            foreach (var request in requests)
            {
                var classId = request.Class.Class.ModelClassId;

                switch (level)
                {
                    case RoomRelaxationLevel.StrictPreferred:
                        if (classPreferences.TryGetValue(classId, out var preference))
                        {
                            var preferredRooms = new HashSet<int>(preference.RoomIds);
                            var filtered = request.CandidateRooms
                                .Where(idx => roomCandidates[idx].Room.BuildingId == preference.BuildingId && preferredRooms.Contains(roomCandidates[idx].Room.RoomId))
                                .ToList();

                            if (filtered.Count == 0)
                            {
                                return false;
                            }

                            request.CandidateRooms = filtered;
                        }
                        else if (request.PreferredRooms.Count > 0)
                        {
                            var preferredIndices = new HashSet<int>(request.PreferredRooms);
                            var filtered = request.CandidateRooms.Where(preferredIndices.Contains).ToList();
                            if (filtered.Count > 0)
                            {
                                request.CandidateRooms = filtered;
                            }
                        }
                        break;

                    case RoomRelaxationLevel.PreferredBuilding:
                        if (classPreferences.TryGetValue(classId, out var buildingPreference))
                        {
                            var filtered = request.CandidateRooms
                                .Where(idx => roomCandidates[idx].Room.BuildingId == buildingPreference.BuildingId)
                                .ToList();

                            if (filtered.Count == 0)
                            {
                                return false;
                            }

                            request.CandidateRooms = filtered;
                        }
                        break;
                }

                if (request.CandidateRooms.Count == 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static int GetSlotDurationMinutes(TimeSlotInfo slot)
        {
            return (int)Math.Round((slot.End - slot.Start).TotalMinutes);
        }

        private static bool TryGetSubjectDuration(SubjectInfo subject, int slotDuration, out int duration)
        {
            var required = subject.Duration > 0 ? subject.Duration : slotDuration;
            if (required > slotDuration)
            {
                duration = 0;
                return false;
            }

            duration = Math.Max(1, required);
            return true;
        }

        private static int GetSubjectDurationForSlot(SubjectInfo subject, TimeSlotInfo slot)
        {
            var slotDuration = GetSlotDurationMinutes(slot);
            if (slotDuration <= 0)
            {
                return 0;
            }

            return TryGetSubjectDuration(subject, slotDuration, out var duration) ? duration : 0;
        }

        private static bool TryGetStartOffset(string? timeText, TimeSlotInfo slot, out int offset)
        {
            offset = 0;
            if (string.IsNullOrWhiteSpace(timeText))
            {
                return false;
            }

            if (!TryParseDateTime(slot.Date, timeText, out var desiredStart))
            {
                return false;
            }

            offset = (int)Math.Round((desiredStart - slot.Start).TotalMinutes);
            return true;
        }

        private static bool ValidateRoomSchedules(
            List<SubjectInfo> slotSubjects,
            SlotRoomAllocationResult allocationResult,
            TimeSlotInfo slot,
            Dictionary<int, int> subjectOffsets,
            AIExamConfig config,
            out SlotTimeConflict? scheduleConflict)
        {
            scheduleConflict = null;

            if (slotSubjects.Count == 0)
            {
                return true;
            }

            var subjectLookup = slotSubjects.ToDictionary(s => s.SubjectId, s => s);
            var subjectRoomMap = new Dictionary<int, HashSet<int>>();

            foreach (var kvp in allocationResult.ClassAllocations)
            {
                var subjectId = kvp.Key.subjectId;
                if (!subjectRoomMap.TryGetValue(subjectId, out var rooms))
                {
                    rooms = new HashSet<int>();
                    subjectRoomMap[subjectId] = rooms;
                }

                foreach (var allocation in kvp.Value)
                {
                    rooms.Add(allocation.Room.RoomId);
                }
            }

            if (subjectRoomMap.Count == 0)
            {
                return true;
            }

            var roomSchedules = new Dictionary<int, List<RoomScheduleEntry>>();
            var minGap = Math.Max(0, config.MinExamInterval);
            var usageLimit = GetRoomUsageLimit(slot.TimeNo);

            foreach (var subjectEntry in subjectRoomMap)
            {
                if (!subjectLookup.TryGetValue(subjectEntry.Key, out var subject))
                {
                    continue;
                }

                var duration = GetSubjectDurationForSlot(subject, slot);
                if (duration <= 0)
                {
                    scheduleConflict = new SlotTimeConflict
                    {
                        TimeIndex = slot.Index,
                        SubjectIds = new List<int> { subjectEntry.Key },
                        ClassIds = CollectClassIdsForSubjects(allocationResult, subjectEntry.Key)
                    };
                    return false;
                }

                var offset = subjectOffsets.TryGetValue(subjectEntry.Key, out var offsetValue) ? offsetValue : 0;
                if (offset < 0)
                {
                    scheduleConflict = new SlotTimeConflict
                    {
                        TimeIndex = slot.Index,
                        SubjectIds = new List<int> { subjectEntry.Key },
                        ClassIds = CollectClassIdsForSubjects(allocationResult, subjectEntry.Key)
                    };
                    return false;
                }

                var startTime = slot.Start.AddMinutes(offset);
                var endTime = startTime.AddMinutes(duration);
                if (startTime < slot.Start || endTime > slot.End)
                {
                    scheduleConflict = new SlotTimeConflict
                    {
                        TimeIndex = slot.Index,
                        SubjectIds = new List<int> { subjectEntry.Key },
                        ClassIds = CollectClassIdsForSubjects(allocationResult, subjectEntry.Key)
                    };
                    return false;
                }

                foreach (var roomId in subjectEntry.Value)
                {
                    if (!roomSchedules.TryGetValue(roomId, out var schedule))
                    {
                        schedule = new List<RoomScheduleEntry>();
                        roomSchedules[roomId] = schedule;
                    }

                    var existingEntry = schedule.FirstOrDefault(e => e.SubjectId == subjectEntry.Key);
                    if (existingEntry != null)
                    {
                        if (existingEntry.Offset != offset || existingEntry.Duration != duration)
                        {
                            scheduleConflict = new SlotTimeConflict
                            {
                                TimeIndex = slot.Index,
                                SubjectIds = new List<int> { subjectEntry.Key },
                                ClassIds = CollectClassIdsForSubjects(allocationResult, subjectEntry.Key)
                            };
                            return false;
                        }
                        continue;
                    }

                    if (usageLimit > 0 && schedule.Count >= usageLimit)
                    {
                        var involvedSubjects = schedule.Select(e => e.SubjectId).Append(subjectEntry.Key).Distinct().ToList();
                        scheduleConflict = new SlotTimeConflict
                        {
                            TimeIndex = slot.Index,
                            SubjectIds = involvedSubjects,
                            ClassIds = CollectClassIdsForSubjects(allocationResult, involvedSubjects.ToArray())
                        };
                        return false;
                    }

                    foreach (var existing in schedule)
                    {
                        var existingStart = slot.Start.AddMinutes(existing.Offset);
                        var existingEnd = existingStart.AddMinutes(existing.Duration);
                        if (!HasSufficientGap(existingStart, existingEnd, startTime, endTime, minGap))
                        {
                            scheduleConflict = new SlotTimeConflict
                            {
                                TimeIndex = slot.Index,
                                SubjectIds = new List<int> { subjectEntry.Key, existing.SubjectId }.Distinct().ToList(),
                                ClassIds = CollectClassIdsForSubjects(allocationResult, subjectEntry.Key, existing.SubjectId)
                            };
                            return false;
                        }
                    }

                    schedule.Add(new RoomScheduleEntry
                    {
                        SubjectId = subjectEntry.Key,
                        Offset = offset,
                        Duration = duration
                    });
                }
            }

            return true;
        }

        private static Dictionary<int, int>? ComputeSubjectStartOffsets(
            List<SubjectInfo> slotSubjects,
            TimeSlotInfo slot,
            SlotRoomAllocationResult allocationResult,
            AIExamConfig config,
            AIExamModel model)
        {
            return TryGenerateSubjectOffsetsWithStepSearch(slotSubjects, slot, allocationResult, config, model);
        }

        private static Dictionary<int, int>? TryGenerateSubjectOffsetsWithStepSearch(
            List<SubjectInfo> slotSubjects,
            TimeSlotInfo slot,
            SlotRoomAllocationResult allocationResult,
            AIExamConfig config,
            AIExamModel model)
        {
            var result = new Dictionary<int, int>();
            if (slotSubjects.Count == 0)
            {
                return result;
            }

            var slotDuration = GetSlotDurationMinutes(slot);
            if (slotDuration <= 0)
            {
                return null;
            }

            var durations = new Dictionary<int, int>();
            foreach (var subject in slotSubjects)
            {
                if (!TryGetSubjectDuration(subject, slotDuration, out var duration))
                {
                    return null;
                }

                durations[subject.SubjectId] = duration;
            }

            var subjectRooms = BuildSubjectRoomUsage(allocationResult, slotSubjects.Select(s => s.SubjectId));
            var candidateMap = BuildSubjectOffsetCandidates(slotSubjects, slot, durations, config, model, slotDuration);
            if (candidateMap == null)
            {
                return null;
            }

            var units = BuildOffsetSearchUnits(slotSubjects, slot, candidateMap, model);
            if (units.Count == 0)
            {
                return FinalizeSubjectOffsets(slotSubjects, result);
            }

            var minGap = Math.Max(0, config.MinExamInterval);

            if (units.All(u => u.PrimaryCandidates.Count > 0))
            {
                var primaryAssignment = TryAssignOffsetsForUnits(units, subjectRooms, durations, slot, minGap, useCombinedCandidates: false);
                if (primaryAssignment != null)
                {
                    return FinalizeSubjectOffsets(slotSubjects, primaryAssignment);
                }
            }

            if (units.Any(u => u.CombinedCandidates.Count == 0))
            {
                return null;
            }

            var fallbackAssignment = TryAssignOffsetsForUnits(units, subjectRooms, durations, slot, minGap, useCombinedCandidates: true);
            return fallbackAssignment == null ? null : FinalizeSubjectOffsets(slotSubjects, fallbackAssignment);
        }

        private static Dictionary<int, int> FinalizeSubjectOffsets(List<SubjectInfo> slotSubjects, Dictionary<int, int> offsets)
        {
            var finalized = new Dictionary<int, int>();
            foreach (var subject in slotSubjects)
            {
                if (!offsets.TryGetValue(subject.SubjectId, out var offset))
                {
                    offset = 0;
                }

                finalized[subject.SubjectId] = offset;
            }

            return finalized;
        }

        private static Dictionary<int, HashSet<int>> BuildSubjectRoomUsage(
            SlotRoomAllocationResult allocationResult,
            IEnumerable<int> subjectIds)
        {
            var subjectSet = new HashSet<int>(subjectIds);
            var usage = new Dictionary<int, HashSet<int>>();

            foreach (var kvp in allocationResult.ClassAllocations)
            {
                if (!subjectSet.Contains(kvp.Key.subjectId))
                {
                    continue;
                }

                if (!usage.TryGetValue(kvp.Key.subjectId, out var rooms))
                {
                    rooms = new HashSet<int>();
                    usage[kvp.Key.subjectId] = rooms;
                }

                foreach (var allocation in kvp.Value)
                {
                    rooms.Add(allocation.Room.RoomId);
                }
            }

            return usage;
        }

        private static Dictionary<int, SubjectOffsetCandidates>? BuildSubjectOffsetCandidates(
            List<SubjectInfo> slotSubjects,
            TimeSlotInfo slot,
            Dictionary<int, int> durations,
            AIExamConfig config,
            AIExamModel model,
            int slotDuration)
        {
            var result = new Dictionary<int, SubjectOffsetCandidates>();
            var step = config.TimeAdjustStepMinutes > 0 ? config.TimeAdjustStepMinutes : 5;
            step = Math.Max(1, step);

            foreach (var subject in slotSubjects)
            {
                var subjectId = subject.SubjectId;
                if (!durations.TryGetValue(subjectId, out var duration))
                {
                    return null;
                }

                var limit = slotDuration - duration;
                if (limit < 0)
                {
                    return null;
                }

                var candidates = new SubjectOffsetCandidates();
                var preferredOffset = GetPreferredStartOffset(subject, slot, model, limit);

                if (preferredOffset.HasValue)
                {
                    candidates.AddForward(preferredOffset.Value);
                }

                for (var offset = 0; offset <= limit; offset += step)
                {
                    candidates.AddForward(offset);
                    if (offset == limit)
                    {
                        break;
                    }

                    if (offset + step > limit && offset != limit)
                    {
                        candidates.AddForward(limit);
                        break;
                    }
                }

                if (!candidates.ForwardAny())
                {
                    candidates.AddForward(0);
                }

                for (var offset = limit; offset >= 0; offset -= step)
                {
                    candidates.AddBackward(offset);
                    if (offset == 0)
                    {
                        break;
                    }

                    if (offset - step < 0 && offset != 0)
                    {
                        candidates.AddBackward(0);
                        break;
                    }
                }

                result[subjectId] = candidates;
            }

            return result;
        }

        private static int? GetPreferredStartOffset(
            SubjectInfo subject,
            TimeSlotInfo slot,
            AIExamModel model,
            int limit)
        {
            int? preferred = null;

            if (!string.IsNullOrWhiteSpace(subject.Subject.StartTime) && TryGetStartOffset(subject.Subject.StartTime, slot, out var subjectOffset))
            {
                if (subjectOffset >= 0 && subjectOffset <= limit)
                {
                    preferred = subjectOffset;
                }
            }

            if (model.RuleJointSubjectList != null)
            {
                foreach (var rule in model.RuleJointSubjectList)
                {
                    if (rule.RuleJointSubjectList == null || string.IsNullOrWhiteSpace(rule.StartTime))
                    {
                        continue;
                    }

                    if (!rule.RuleJointSubjectList.Any(r => r.ModelSubjectId == subject.SubjectId))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(rule.Date) && !rule.Date.Equals(slot.Date, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryGetStartOffset(rule.StartTime, slot, out var ruleOffset) && ruleOffset >= 0 && ruleOffset <= limit)
                    {
                        preferred = ruleOffset;
                        break;
                    }
                }
            }

            return preferred;
        }

        private static List<OffsetSearchUnit> BuildOffsetSearchUnits(
            List<SubjectInfo> slotSubjects,
            TimeSlotInfo slot,
            Dictionary<int, SubjectOffsetCandidates> candidates,
            AIExamModel model)
        {
            var groups = BuildJointSubjectGroups(slotSubjects, slot, model);
            var units = new List<OffsetSearchUnit>();

            foreach (var group in groups)
            {
                var unit = new OffsetSearchUnit
                {
                    SubjectIds = group.OrderBy(id => id).ToList(),
                    PrimaryCandidates = BuildUnitCandidates(group, candidates, includeBackward: false),
                    CombinedCandidates = BuildUnitCandidates(group, candidates, includeBackward: true)
                };

                units.Add(unit);
            }

            return units;
        }

        private static List<List<int>> BuildJointSubjectGroups(
            List<SubjectInfo> slotSubjects,
            TimeSlotInfo slot,
            AIExamModel model)
        {
            var parents = new Dictionary<int, int>();
            foreach (var subject in slotSubjects)
            {
                parents[subject.SubjectId] = subject.SubjectId;
            }

            int Find(int id)
            {
                if (parents[id] != id)
                {
                    parents[id] = Find(parents[id]);
                }

                return parents[id];
            }

            void Union(int a, int b)
            {
                var rootA = Find(a);
                var rootB = Find(b);
                if (rootA == rootB)
                {
                    return;
                }

                parents[rootB] = rootA;
            }

            if (model.RuleJointSubjectList != null)
            {
                foreach (var rule in model.RuleJointSubjectList)
                {
                    if (rule.RuleJointSubjectList == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(rule.Date) && !rule.Date.Equals(slot.Date, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var related = rule.RuleJointSubjectList
                        .Select(item => item.ModelSubjectId)
                        .Where(parents.ContainsKey)
                        .Distinct()
                        .ToList();

                    if (related.Count <= 1)
                    {
                        continue;
                    }

                    var anchor = related[0];
                    foreach (var subjectId in related.Skip(1))
                    {
                        Union(anchor, subjectId);
                    }
                }
            }

            var groups = new Dictionary<int, List<int>>();
            foreach (var subjectId in parents.Keys)
            {
                var root = Find(subjectId);
                if (!groups.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    groups[root] = list;
                }

                list.Add(subjectId);
            }

            return groups.Values.Select(list => list.OrderBy(id => id).ToList()).ToList();
        }

        private static List<int> BuildUnitCandidates(
            List<int> subjectIds,
            Dictionary<int, SubjectOffsetCandidates> candidates,
            bool includeBackward)
        {
            List<int>? baseList = null;
            var baseSubjectId = -1;

            foreach (var subjectId in subjectIds)
            {
                if (!candidates.TryGetValue(subjectId, out var subjectCandidates))
                {
                    return new List<int>();
                }

                var list = includeBackward ? subjectCandidates.GetCombined() : subjectCandidates.Forward;
                if (list.Count == 0)
                {
                    return new List<int>();
                }

                if (baseList == null || list.Count < baseList.Count)
                {
                    baseList = new List<int>(list);
                    baseSubjectId = subjectId;
                }
            }

            if (baseList == null)
            {
                return new List<int>();
            }

            foreach (var subjectId in subjectIds)
            {
                if (subjectId == baseSubjectId)
                {
                    continue;
                }

                var list = includeBackward ? candidates[subjectId].GetCombined() : candidates[subjectId].Forward;
                var allowed = new HashSet<int>(list);
                baseList = baseList.Where(allowed.Contains).ToList();

                if (baseList.Count == 0)
                {
                    break;
                }
            }

            return baseList;
        }

        private static Dictionary<int, int>? TryAssignOffsetsForUnits(
            List<OffsetSearchUnit> units,
            Dictionary<int, HashSet<int>> subjectRooms,
            Dictionary<int, int> durations,
            TimeSlotInfo slot,
            int minGap,
            bool useCombinedCandidates)
        {
            var orderedUnits = units
                .Select(unit => new
                {
                    Unit = unit,
                    Candidates = useCombinedCandidates ? unit.CombinedCandidates : unit.PrimaryCandidates
                })
                .OrderBy(x => x.Candidates.Count)
                .ThenBy(x => x.Unit.SubjectIds.Count)
                .Select(x => x.Unit)
                .ToList();

            var assignments = new Dictionary<int, int>();
            var roomSchedules = new Dictionary<int, List<RoomScheduleEntry>>();

            bool Dfs(int index)
            {
                if (index >= orderedUnits.Count)
                {
                    return true;
                }

                var unit = orderedUnits[index];
                var candidates = useCombinedCandidates ? unit.CombinedCandidates : unit.PrimaryCandidates;

                if (candidates.Count == 0)
                {
                    return false;
                }

                foreach (var offset in candidates)
                {
                    if (!TryAssignUnit(unit, offset, assignments, roomSchedules, subjectRooms, durations, slot, minGap, out var added))
                    {
                        continue;
                    }

                    if (Dfs(index + 1))
                    {
                        return true;
                    }

                    UndoAssignment(unit, assignments, roomSchedules, added);
                }

                return false;
            }

            return Dfs(0) ? new Dictionary<int, int>(assignments) : null;
        }

        private static bool TryAssignUnit(
            OffsetSearchUnit unit,
            int offset,
            Dictionary<int, int> assignments,
            Dictionary<int, List<RoomScheduleEntry>> roomSchedules,
            Dictionary<int, HashSet<int>> subjectRooms,
            Dictionary<int, int> durations,
            TimeSlotInfo slot,
            int minGap,
            out List<(int roomId, RoomScheduleEntry entry)> addedEntries)
        {
            addedEntries = new List<(int, RoomScheduleEntry)>();
            var startTime = slot.Start.AddMinutes(offset);

            foreach (var subjectId in unit.SubjectIds)
            {
                var duration = durations[subjectId];
                if (startTime.AddMinutes(duration) > slot.End)
                {
                    return false;
                }

                if (!subjectRooms.TryGetValue(subjectId, out var rooms) || rooms.Count == 0)
                {
                    continue;
                }

                foreach (var roomId in rooms)
                {
                    if (!roomSchedules.TryGetValue(roomId, out var schedule))
                    {
                        continue;
                    }

                    foreach (var entry in schedule)
                    {
                        var existingStart = slot.Start.AddMinutes(entry.Offset);
                        var existingEnd = existingStart.AddMinutes(entry.Duration);
                        var candidateStart = startTime;
                        var candidateEnd = candidateStart.AddMinutes(duration);

                        if (!HasSufficientGap(existingStart, existingEnd, candidateStart, candidateEnd, minGap))
                        {
                            return false;
                        }
                    }
                }
            }

            foreach (var subjectId in unit.SubjectIds)
            {
                assignments[subjectId] = offset;
                if (!subjectRooms.TryGetValue(subjectId, out var rooms) || rooms.Count == 0)
                {
                    continue;
                }

                foreach (var roomId in rooms)
                {
                    if (!roomSchedules.TryGetValue(roomId, out var schedule))
                    {
                        schedule = new List<RoomScheduleEntry>();
                        roomSchedules[roomId] = schedule;
                    }

                    var entry = new RoomScheduleEntry
                    {
                        SubjectId = subjectId,
                        Offset = offset,
                        Duration = durations[subjectId]
                    };

                    schedule.Add(entry);
                    addedEntries.Add((roomId, entry));
                }
            }

            return true;
        }

        private static void UndoAssignment(
            OffsetSearchUnit unit,
            Dictionary<int, int> assignments,
            Dictionary<int, List<RoomScheduleEntry>> roomSchedules,
            List<(int roomId, RoomScheduleEntry entry)> addedEntries)
        {
            foreach (var subjectId in unit.SubjectIds)
            {
                assignments.Remove(subjectId);
            }

            for (var index = addedEntries.Count - 1; index >= 0; index--)
            {
                var group = addedEntries[index];
                if (!roomSchedules.TryGetValue(group.roomId, out var schedule))
                {
                    continue;
                }

                for (var i = schedule.Count - 1; i >= 0; i--)
                {
                    if (schedule[i].SubjectId == group.entry.SubjectId && schedule[i].Offset == group.entry.Offset)
                    {
                        schedule.RemoveAt(i);
                        break;
                    }
                }

                if (schedule.Count == 0)
                {
                    roomSchedules.Remove(group.roomId);
                }
            }
        }
        private static List<int> CollectClassIdsForSubjects(
            SlotRoomAllocationResult allocationResult,
            params int[] subjectIds)
        {
            var subjectSet = new HashSet<int>(subjectIds);
            return allocationResult.ClassAllocations
                .Where(kvp => subjectSet.Contains(kvp.Key.subjectId))
                .Select(kvp => kvp.Key.classId)
                .Distinct()
                .ToList();
        }

        private static bool HasSufficientGap(DateTime startA, DateTime endA, DateTime startB, DateTime endB, int minGapMinutes)
        {
            var gap = Math.Max(0, minGapMinutes);
            if (startA <= startB)
            {
                return startB >= endA.AddMinutes(gap);
            }

            return startA >= endB.AddMinutes(gap);
        }

        private static SlotRoomAllocationResult? SolveSlotRoomAllocationWithCp(
            List<SlotClassRequest> slotClasses,
            List<RoomCandidate> roomCandidates,
            Dictionary<int, ClassRoomPreference> classPreferences,
            TimeSlotInfo slot,
            AIExamConfig config,
            List<SubjectInfo> slotSubjects,
            AIExamModel model)
        {
            if (slotClasses.Count == 0 || roomCandidates.Count == 0)
            {
                return null;
            }

            var slotDuration = (int)Math.Round((slot.End - slot.Start).TotalMinutes);
            if (slotDuration <= 0)
            {
                return null;
            }

            var cpModel = new CpModel();
            var classCount = slotClasses.Count;
            var roomCount = roomCandidates.Count;

            var subjectLookup = slotSubjects
                .GroupBy(s => s.SubjectId)
                .Select(g => g.First())
                .ToDictionary(s => s.SubjectId, s => s);

            var durations = new Dictionary<int, int>();
            foreach (var subject in subjectLookup.Values)
            {
                if (!TryGetSubjectDuration(subject, slotDuration, out var duration))
                {
                    return null;
                }

                durations[subject.SubjectId] = duration;
            }

            var fixedOffsets = new Dictionary<int, int>();
            foreach (var subject in subjectLookup.Values)
            {
                if (TryGetStartOffset(subject.Subject.StartTime, slot, out var fixedOffset))
                {
                    var maxOffset = slotDuration - durations[subject.SubjectId];
                    if (fixedOffset < 0 || fixedOffset > maxOffset)
                    {
                        return null;
                    }

                    fixedOffsets[subject.SubjectId] = fixedOffset;
                }
            }

            if (model.RuleJointSubjectList != null)
            {
                foreach (var rule in model.RuleJointSubjectList)
                {
                    if (rule.RuleJointSubjectList == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(rule.Date) &&
                        !rule.Date.Equals(slot.Date, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var group = rule.RuleJointSubjectList
                        .Select(item => item.ModelSubjectId)
                        .Where(subjectLookup.ContainsKey)
                        .Distinct()
                        .ToList();

                    if (group.Count == 0)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(rule.StartTime))
                    {
                        if (!TryGetStartOffset(rule.StartTime, slot, out var groupOffset))
                        {
                            return null;
                        }

                        foreach (var subjectId in group)
                        {
                            var maxOffset = slotDuration - durations[subjectId];
                            if (groupOffset < 0 || groupOffset > maxOffset)
                            {
                                return null;
                            }

                            if (fixedOffsets.TryGetValue(subjectId, out var existing) && existing != groupOffset)
                            {
                                return null;
                            }

                            fixedOffsets[subjectId] = groupOffset;
                        }
                    }
                }
            }

            var startVars = new Dictionary<int, IntVar>();
            foreach (var subject in subjectLookup.Values)
            {
                var duration = durations[subject.SubjectId];
                var maxOffset = slotDuration - duration;
                if (maxOffset < 0)
                {
                    return null;
                }

                var startVar = cpModel.NewIntVar(0, maxOffset, $"slot_{slot.Index}_subject_{subject.SubjectId}_start");
                cpModel.Add(startVar + duration <= slotDuration);

                if (fixedOffsets.TryGetValue(subject.SubjectId, out var fixedOffset))
                {
                    cpModel.Add(startVar == fixedOffset);
                }

                startVars[subject.SubjectId] = startVar;
            }

            if (model.RuleJointSubjectList != null)
            {
                foreach (var rule in model.RuleJointSubjectList)
                {
                    if (rule.RuleJointSubjectList == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(rule.Date) &&
                        !rule.Date.Equals(slot.Date, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var group = rule.RuleJointSubjectList
                        .Select(item => item.ModelSubjectId)
                        .Where(startVars.ContainsKey)
                        .Distinct()
                        .ToList();

                    if (group.Count <= 1)
                    {
                        continue;
                    }

                    var anchor = group[0];
                    foreach (var subjectId in group.Skip(1))
                    {
                        cpModel.Add(startVars[subjectId] == startVars[anchor]);
                    }
                }
            }

            var xVars = new IntVar?[classCount, roomCount];
            var yVars = new BoolVar?[classCount, roomCount];

            for (var i = 0; i < classCount; i++)
            {
                var candidates = slotClasses[i].CandidateRooms;
                if (candidates.Count == 0)
                {
                    return null;
                }

                foreach (var roomIndex in candidates)
                {
                    var capacity = roomCandidates[roomIndex].AvailableSeats;
                    var classId = slotClasses[i].Class.Class.ModelClassId;
                    var roomId = roomCandidates[roomIndex].Room.RoomId;

                    var xVar = cpModel.NewIntVar(0, capacity, $"slot_cls_{classId}_room_{roomId}_students");
                    var yVar = cpModel.NewBoolVar($"slot_cls_{classId}_room_{roomId}_use");

                    cpModel.Add(xVar <= capacity * yVar);
                    cpModel.Add(xVar >= 1).OnlyEnforceIf(yVar);
                    cpModel.Add(xVar == 0).OnlyEnforceIf(yVar.Not());

                    xVars[i, roomIndex] = xVar;
                    yVars[i, roomIndex] = yVar;
                }
            }

            for (var i = 0; i < classCount; i++)
            {
                var studentVars = new List<IntVar>();
                for (var j = 0; j < roomCount; j++)
                {
                    if (xVars[i, j] is not null)
                    {
                        studentVars.Add(xVars[i, j]!);
                    }
                }

                if (studentVars.Count == 0)
                {
                    return null;
                }

                cpModel.Add(LinearExpr.Sum(studentVars.ToArray()) == slotClasses[i].Class.StudentCount);
            }

            for (var j = 0; j < roomCount; j++)
            {
                var loadVars = new List<IntVar>();
                for (var i = 0; i < classCount; i++)
                {
                    if (xVars[i, j] is not null)
                    {
                        loadVars.Add(xVars[i, j]!);
                    }
                }

                if (loadVars.Count > 0)
                {
                    cpModel.Add(LinearExpr.Sum(loadVars.ToArray()) <= roomCandidates[j].AvailableSeats);
                }
            }

            var classesBySubject = slotClasses
                .Select((cls, index) => new { cls, index })
                .GroupBy(x => x.cls.Subject.SubjectId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.index).ToList());

            for (var j = 0; j < roomCount; j++)
            {
                var subjectVars = new List<(int subjectId, BoolVar variable)>();
                foreach (var kv in classesBySubject)
                {
                    var relevantIndexes = kv.Value.Where(idx => yVars[idx, j] is not null).ToList();
                    if (relevantIndexes.Count == 0)
                    {
                        continue;
                    }

                    var subjectUsageVars = relevantIndexes
                        .Select(idx => yVars[idx, j]!)
                        .ToArray();

                    var gradeIndicators = new List<BoolVar>();
                    foreach (var gradeGroup in relevantIndexes
                        .GroupBy(idx => slotClasses[idx].Class.Grade))
                    {
                        var gradeIndex = gradeGroup.First();
                        var gradeValue = slotClasses[gradeIndex].Class.Grade;
                        var gradeVar = cpModel.NewBoolVar($"room_{roomCandidates[j].Room.RoomId}_subject_{kv.Key}_grade_{gradeValue}");

                        foreach (var idx in gradeGroup)
                        {
                            cpModel.Add(yVars[idx, j]! <= gradeVar);
                        }

                        var usageExpr = LinearExpr.Sum(gradeGroup.Select(idx => yVars[idx, j]!).ToArray());
                        cpModel.Add(gradeVar <= usageExpr);
                        cpModel.Add(usageExpr <= 1);
                        gradeIndicators.Add(gradeVar);
                    }

                    if (gradeIndicators.Count > 0)
                    {
                        cpModel.Add(LinearExpr.Sum(gradeIndicators.ToArray()) <= 2);
                    }

                    var subjectVar = cpModel.NewBoolVar($"room_{roomCandidates[j].Room.RoomId}_subject_{kv.Key}");
                    foreach (var idx in relevantIndexes)
                    {
                        cpModel.Add(yVars[idx, j]! <= subjectVar);
                    }

                    cpModel.Add(subjectVar <= LinearExpr.Sum(relevantIndexes.Select(idx => yVars[idx, j]!).ToArray()));
                    subjectVars.Add((kv.Key, subjectVar));
                }

                if (subjectVars.Count > 0)
                {
                    var subjectBoolVars = subjectVars.Select(v => v.variable).ToArray();
                    var durationExpr = LinearExpr.Sum(subjectVars.Select(v => v.variable * durations[v.subjectId]).ToArray());
                    var countVar = cpModel.NewIntVar(0, subjectVars.Count, $"room_{roomCandidates[j].Room.RoomId}_subject_count");
                    cpModel.Add(countVar == LinearExpr.Sum(subjectBoolVars));

                    if (config.MinExamInterval > 0)
                    {
                        cpModel.Add(durationExpr + config.MinExamInterval * countVar <= slotDuration + config.MinExamInterval);
                    }
                    else
                    {
                        cpModel.Add(durationExpr <= slotDuration);
                    }

                    var gap = Math.Max(0, config.MinExamInterval);
                    for (var a = 0; a < subjectVars.Count; a++)
                    {
                        for (var b = a + 1; b < subjectVars.Count; b++)
                        {
                            var subjectA = subjectVars[a].subjectId;
                            var subjectB = subjectVars[b].subjectId;

                            if (!startVars.ContainsKey(subjectA) || !startVars.ContainsKey(subjectB))
                            {
                                continue;
                            }

                            var subjectVarA = subjectVars[a].variable;
                            var subjectVarB = subjectVars[b].variable;

                            var aBeforeB = cpModel.NewBoolVar($"room_{roomCandidates[j].Room.RoomId}_subjects_{subjectA}_{subjectB}_a_before_b");
                            var bBeforeA = cpModel.NewBoolVar($"room_{roomCandidates[j].Room.RoomId}_subjects_{subjectA}_{subjectB}_b_before_a");

                            cpModel.Add(aBeforeB <= subjectVarA);
                            cpModel.Add(aBeforeB <= subjectVarB);
                            cpModel.Add(bBeforeA <= subjectVarA);
                            cpModel.Add(bBeforeA <= subjectVarB);

                            cpModel.Add(LinearExpr.Sum(new IntVar[] { aBeforeB, bBeforeA }) <= 1);
                            cpModel.Add(LinearExpr.Sum(new IntVar[] { aBeforeB, bBeforeA }) >=
                                LinearExpr.Sum(new IntVar[] { subjectVarA, subjectVarB }) - 1);

                            cpModel.Add(startVars[subjectA] + durations[subjectA] + gap <= startVars[subjectB])
                                .OnlyEnforceIf(aBeforeB);
                            cpModel.Add(startVars[subjectB] + durations[subjectB] + gap <= startVars[subjectA])
                                .OnlyEnforceIf(bBeforeA);
                        }
                    }
                }
            }

            var roomsByBuilding = roomCandidates
                .Select((candidate, index) => new { candidate, index })
                .GroupBy(x => x.candidate.Room.BuildingId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.index).ToList());

            var classBuildingSelections = new List<Dictionary<int, BoolVar>>();
            var preferencePenaltyVars = new List<BoolVar>();

            for (var i = 0; i < classCount; i++)
            {
                var buildingVars = new Dictionary<int, BoolVar>();
                var buildingVarList = new List<BoolVar>();

                foreach (var kv in roomsByBuilding)
                {
                    var candidateIndexes = kv.Value.Where(idx => yVars[i, idx] is not null).ToList();
                    if (candidateIndexes.Count == 0)
                    {
                        continue;
                    }

                    var buildingVar = cpModel.NewBoolVar($"cls_{slotClasses[i].Class.Class.ModelClassId}_building_{kv.Key}");
                    buildingVars[kv.Key] = buildingVar;

                    foreach (var roomIndex in candidateIndexes)
                    {
                        cpModel.Add(yVars[i, roomIndex]! <= buildingVar);
                    }

                    cpModel.Add(buildingVar <= LinearExpr.Sum(candidateIndexes.Select(idx => yVars[i, idx]!).ToArray()));
                    buildingVarList.Add(buildingVar);
                }

                if (buildingVarList.Count == 0)
                {
                    return null;
                }

                cpModel.Add(LinearExpr.Sum(buildingVarList.ToArray()) == 1);
                classBuildingSelections.Add(buildingVars);

                if (classPreferences.TryGetValue(slotClasses[i].Class.Class.ModelClassId, out var preference) && preference != null && buildingVars.TryGetValue(preference.BuildingId, out var preferredVar))
                {
                    var keepPreferred = cpModel.NewBoolVar($"cls_{slotClasses[i].Class.Class.ModelClassId}_keep_pref_building");
                    cpModel.Add(keepPreferred == 1).OnlyEnforceIf(preferredVar);
                    cpModel.Add(keepPreferred == 0).OnlyEnforceIf(preferredVar.Not());

                    var changePreferred = cpModel.NewBoolVar($"cls_{slotClasses[i].Class.Class.ModelClassId}_change_pref_building");
                    cpModel.Add(changePreferred + keepPreferred == 1);
                    preferencePenaltyVars.Add(changePreferred);
                }

                if (slotClasses[i].PreferredRooms.Count > 0)
                {
                    var preferredSet = new HashSet<int>(slotClasses[i].PreferredRooms);
                    var nonPreferredVars = new List<BoolVar>();

                    for (var j = 0; j < roomCount; j++)
                    {
                        if (yVars[i, j] is null)
                        {
                            continue;
                        }

                        if (!preferredSet.Contains(j))
                        {
                            nonPreferredVars.Add(yVars[i, j]!);
                        }
                    }

                    if (nonPreferredVars.Count > 0)
                    {
                        var leavePreferred = cpModel.NewBoolVar($"cls_{slotClasses[i].Class.Class.ModelClassId}_leave_pref_rooms");
                        foreach (var indicator in nonPreferredVars)
                        {
                            cpModel.Add(indicator <= leavePreferred);
                        }

                        cpModel.Add(leavePreferred <= LinearExpr.Sum(nonPreferredVars.ToArray()));
                        preferencePenaltyVars.Add(leavePreferred);
                    }
                }
            }

            var seatWasteVars = new List<IntVar>();
            for (var j = 0; j < roomCount; j++)
            {
                var capacity = roomCandidates[j].AvailableSeats;
                var usageVar = cpModel.NewIntVar(0, capacity, $"room_{roomCandidates[j].Room.RoomId}_used");
                var load = new List<IntVar>();
                for (var i = 0; i < classCount; i++)
                {
                    if (xVars[i, j] is not null)
                    {
                        load.Add(xVars[i, j]!);
                    }
                }

                if (load.Count == 0)
                {
                    cpModel.Add(usageVar == 0);
                }
                else
                {
                    cpModel.Add(usageVar == LinearExpr.Sum(load.ToArray()));
                }

                var wasteVar = cpModel.NewIntVar(0, capacity, $"room_{roomCandidates[j].Room.RoomId}_waste");
                cpModel.Add(wasteVar == capacity - usageVar);
                seatWasteVars.Add(wasteVar);
            }

            var roomUsageVars = new List<IntVar>();
            for (var i = 0; i < classCount; i++)
            {
                var usageIndicators = new List<BoolVar>();
                for (var j = 0; j < roomCount; j++)
                {
                    if (yVars[i, j] is not null)
                    {
                        usageIndicators.Add(yVars[i, j]!);
                    }
                }

                if (usageIndicators.Count == 0)
                {
                    return null;
                }

                var roomCountVar = cpModel.NewIntVar(1, usageIndicators.Count, $"cls_{slotClasses[i].Class.Class.ModelClassId}_room_count");
                cpModel.Add(roomCountVar == LinearExpr.Sum(usageIndicators.ToArray()));
                roomUsageVars.Add(roomCountVar);
            }

            var objectiveTerms = new List<LinearExpr>();
            if (seatWasteVars.Count > 0)
            {
                objectiveTerms.Add(LinearExpr.Sum(seatWasteVars.Select(v => v * 5L).ToArray()));
            }

            if (roomUsageVars.Count > 0)
            {
                objectiveTerms.Add(LinearExpr.Sum(roomUsageVars.Select(v => v * 20L).ToArray()));
            }

            if (preferencePenaltyVars.Count > 0)
            {
                objectiveTerms.Add(LinearExpr.Sum(preferencePenaltyVars.Select(v => v * 50L).ToArray()));
            }

            if (objectiveTerms.Count > 0)
            {
                cpModel.Minimize(LinearExpr.Sum(objectiveTerms.ToArray()));
            }
            else
            {
                cpModel.Minimize(LinearExpr.Constant(0));
            }

            var solver = new CpSolver
            {
                StringParameters = "max_time_in_seconds:30"
            };

            var status = solver.Solve(cpModel);
            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                return null;
            }

            var result = new SlotRoomAllocationResult();
            foreach (var subject in subjectLookup.Values)
            {
                if (startVars.TryGetValue(subject.SubjectId, out var startVar))
                {
                    result.SubjectStartOffsets[subject.SubjectId] = (int)solver.Value(startVar);
                }
            }

            for (var i = 0; i < classCount; i++)
            {
                var request = slotClasses[i];
                var allocations = new List<RoomAllocation>();

                foreach (var roomIndex in request.CandidateRooms)
                {
                    var variable = xVars[i, roomIndex];
                    if (variable == null)
                    {
                        continue;
                    }

                    var assigned = (int)solver.Value(variable);
                    if (assigned <= 0)
                    {
                        continue;
                    }

                    allocations.Add(new RoomAllocation
                    {
                        Room = roomCandidates[roomIndex].Room,
                        Students = assigned
                    });
                }

                if (allocations.Count == 0)
                {
                    return null;
                }

                result.ClassAllocations[(request.Subject.SubjectId, request.Class.Class.ModelClassId)] = allocations;

                if (classBuildingSelections.Count > i)
                {
                    foreach (var kv in classBuildingSelections[i])
                    {
                        if (solver.Value(kv.Value) == 1)
                        {
                            result.ClassBuilding[request.Class.Class.ModelClassId] = kv.Key;
                            break;
                        }
                    }
                }
            }

            var stepOffsets = ComputeSubjectStartOffsets(slotSubjects, slot, result, config, model);
            if (stepOffsets == null)
            {
                return null;
            }

            result.SubjectStartOffsets.Clear();
            foreach (var kv in stepOffsets)
            {
                result.SubjectStartOffsets[kv.Key] = kv.Value;
            }

            return result;
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

        private static Dictionary<(int timeIndex, int roomId, int subjectId), List<int>>? AssignTeachers(
            List<TeacherInfo> teachers,
            List<RoomEvent> roomEvents,
            AIExamModel model,
            StringBuilder error)
        {
            var result = new Dictionary<(int timeIndex, int roomId, int subjectId), List<int>>();
            foreach (var evt in roomEvents)
            {
                result[(evt.Slot.Index, evt.Room.RoomId, evt.Subject.SubjectId)] = new List<int>();
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
            var genderImbalanceVars = new List<IntVar>();

            var eventsByIndex = eventsRequiringTeacher.Select((evt, index) => (evt, index)).ToList();

            foreach (var (evt, index) in eventsByIndex)
            {
                var varsForEvent = new List<BoolVar>();
                var maleVars = new List<BoolVar>();
                var femaleVars = new List<BoolVar>();

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
                    else if (teacher.Gender == 2)
                    {
                        femaleVars.Add(variable);
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

                var femaleCountVar = cpModel.NewIntVar(0, evt.Room.TeacherCount, $"female_cnt_{index}");
                if (femaleVars.Count > 0)
                {
                    cpModel.Add(femaleCountVar == LinearExpr.Sum(femaleVars));
                }
                else
                {
                    cpModel.Add(femaleCountVar == 0);
                }

                cpModel.Add(maleCountVar + femaleCountVar <= evt.Room.TeacherCount);

                var imbalanceVar = cpModel.NewIntVar(0, evt.Room.TeacherCount, $"gender_imbalance_{index}");
                cpModel.AddAbsEquality(imbalanceVar, maleCountVar - femaleCountVar);
                genderImbalanceVars.Add(imbalanceVar);
            }

            foreach (var teacher in teachers)
            {
                var vars = teacherEventVars[teacher.TeacherId];
                if (vars.Count == 0)
                {
                    continue;
                }

                var dayRoomEvents = new Dictionary<DateOnly, Dictionary<int, List<BoolVar>>>();
                foreach (var (evt, index) in eventsByIndex)
                {
                    if (!assignmentVars.TryGetValue((teacher.TeacherId, index), out var variable))
                    {
                        continue;
                    }

                    var dayKey = DateOnly.FromDateTime(evt.Slot.Start);
                    if (!dayRoomEvents.TryGetValue(dayKey, out var roomMap))
                    {
                        roomMap = new Dictionary<int, List<BoolVar>>();
                        dayRoomEvents[dayKey] = roomMap;
                    }

                    if (!roomMap.TryGetValue(evt.Room.RoomId, out var eventList))
                    {
                        eventList = new List<BoolVar>();
                        roomMap[evt.Room.RoomId] = eventList;
                    }

                    eventList.Add(variable);

                }

                var dayRoomSelectionVars = new Dictionary<DateOnly, List<BoolVar>>();
                foreach (var (day, roomMap) in dayRoomEvents)
                {
                    foreach (var (roomId, eventList) in roomMap)
                    {
                        var dayLabel = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                        var dayRoomVar = cpModel.NewBoolVar($"teacher_{teacher.TeacherId}_day_{dayLabel}_room_{roomId}");

                        foreach (var evtVar in eventList)
                        {
                            cpModel.Add(evtVar <= dayRoomVar);
                        }

                        cpModel.Add(dayRoomVar <= LinearExpr.Sum(eventList));

                        if (!dayRoomSelectionVars.TryGetValue(day, out var selectionList))
                        {
                            selectionList = new List<BoolVar>();
                            dayRoomSelectionVars[day] = selectionList;
                        }

                        selectionList.Add(dayRoomVar);
                    }
                }

                foreach (var kv in dayRoomSelectionVars)
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

            var minLoad = cpModel.NewIntVar(0, eventsRequiringTeacher.Count, "min_teacher_load");
            foreach (var load in teacherLoads.Values)
            {
                cpModel.Add(load >= minLoad);
            }

            var loadSpan = cpModel.NewIntVar(0, eventsRequiringTeacher.Count, "teacher_load_span");
            cpModel.Add(maxLoad - minLoad <= loadSpan);

            cpModel.Add(minLoad <= maxLoad);

            var objectiveTerms = new List<LinearExpr>
            {
                LinearExpr.Term(maxLoad, 1000),
                LinearExpr.Term(loadSpan, 100)
            };

            if (genderImbalanceVars.Count > 0)
            {
                objectiveTerms.Add(LinearExpr.Sum(genderImbalanceVars));
            }

            cpModel.Minimize(LinearExpr.Sum(objectiveTerms));

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

                result[(evt.Slot.Index, evt.Room.RoomId, evt.Subject.SubjectId)] = teachersForEvent;
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

        private static int GetRoomUsageLimit(string? timeNo)
        {
            if (string.IsNullOrWhiteSpace(timeNo))
            {
                return 0;
            }

            switch (timeNo.Trim())
            {
                case "上午场":
                    return 2;
                case "下午场":
                    return 3;
                case "晚上场":
                    return 1;
                default:
                    return 0;
            }
        }

        #endregion

        private static List<AIExamResult> BuildResults(RoomAssignmentContainer container,
            Dictionary<(int timeIndex, int roomId, int subjectId), List<int>> teacherAssignments)
        {
            var results = new List<AIExamResult>();

            foreach (var assignment in container.ClassAssignments)
            {
                var timeIndex = assignment.Slot.Index;
                var start = assignment.Slot.Start.AddMinutes(assignment.StartOffsetMinutes);
                var duration = assignment.Subject.Duration > 0 ? assignment.Subject.Duration : (int)(assignment.Slot.End - assignment.Slot.Start).TotalMinutes;
                var end = start.AddMinutes(duration);
                if (end > assignment.Slot.End)
                {
                    end = assignment.Slot.End;
                }

                var teacherList = teacherAssignments.TryGetValue((timeIndex, assignment.Room.RoomId, assignment.Subject.SubjectId), out var assignedTeachers)
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
            public int Order { get; set; }
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

        private sealed class SubjectOffsetCandidates
        {
            private readonly HashSet<int> _forwardSet = new HashSet<int>();
            private readonly HashSet<int> _backwardSet = new HashSet<int>();
            private List<int>? _combined;

            public List<int> Forward { get; } = new List<int>();
            public List<int> Backward { get; } = new List<int>();

            public void AddForward(int offset)
            {
                if (_forwardSet.Add(offset))
                {
                    Forward.Add(offset);
                    _combined = null;
                }
            }

            public void AddBackward(int offset)
            {
                if (_forwardSet.Contains(offset))
                {
                    return;
                }

                if (_backwardSet.Add(offset))
                {
                    Backward.Add(offset);
                    _combined = null;
                }
            }

            public bool ForwardAny()
            {
                return Forward.Count > 0;
            }

            public List<int> GetCombined()
            {
                if (_combined != null)
                {
                    return _combined;
                }

                var combined = new List<int>(Forward);
                var seen = new HashSet<int>(_forwardSet);
                foreach (var offset in Backward)
                {
                    if (seen.Add(offset))
                    {
                        combined.Add(offset);
                    }
                }

                _combined = combined;
                return combined;
            }
        }

        private sealed class OffsetSearchUnit
        {
            public List<int> SubjectIds { get; set; } = new List<int>();
            public List<int> PrimaryCandidates { get; set; } = new List<int>();
            public List<int> CombinedCandidates { get; set; } = new List<int>();
        }

        private sealed class RoomScheduleEntry
        {
            public int SubjectId { get; set; }
            public int Offset { get; set; }
            public int Duration { get; set; }
        }

        private sealed class SubjectTimeAdjustmentState
        {
            public SubjectInfo Subject { get; set; } = null!;
            public string? PreferredDate { get; set; }
            public DateTime? PreferredStart { get; set; }
            public int RequiredDuration { get; set; }
            public int MaxToleranceMinutes { get; set; }
            public int CurrentToleranceMinutes { get; set; }
            public List<int> OrderedCandidates { get; } = new List<int>();
            public Dictionary<int, int> CandidateDistance { get; } = new Dictionary<int, int>();
            public List<int> ActiveCandidates { get; } = new List<int>();
        }

        private sealed class ClassRoomAssignment
        {
            public SubjectInfo Subject { get; set; } = null!;
            public ClassInfo Class { get; set; } = null!;
            public RoomInfo Room { get; set; } = null!;
            public TimeSlotInfo Slot { get; set; } = null!;
            public int Students { get; set; }
            public int StartOffsetMinutes { get; set; }
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
            public int StartOffsetMinutes { get; set; }
            public DateTime StartTime => Slot.Start.AddMinutes(StartOffsetMinutes);
            public DateTime EndTime
            {
                get
                {
                    var duration = Subject.Duration > 0
                        ? Subject.Duration
                        : (int)(Slot.End - Slot.Start).TotalMinutes;
                    var end = StartTime.AddMinutes(duration);
                    return end <= Slot.End ? end : Slot.End;
                }
            }
        }

        private sealed class RoomCandidate
        {
            public RoomInfo Room { get; set; } = null!;
            public int AvailableSeats => Room.SeatCount;
        }

        private sealed class RoomAllocation
        {
            public RoomInfo Room { get; set; } = null!;
            public int Students { get; set; }
        }

        private sealed class SlotClassRequest
        {
            public SubjectInfo Subject { get; set; } = null!;
            public ClassInfo Class { get; set; } = null!;
            public List<int> CandidateRooms { get; set; } = new List<int>();
            public List<int> PreferredRooms { get; set; } = new List<int>();
        }

        private sealed class SlotRoomAllocationResult
        {
            public Dictionary<(int subjectId, int classId), List<RoomAllocation>> ClassAllocations { get; } = new Dictionary<(int subjectId, int classId), List<RoomAllocation>>();
            public Dictionary<int, int> ClassBuilding { get; } = new Dictionary<int, int>();
            public Dictionary<int, int> SubjectStartOffsets { get; } = new Dictionary<int, int>();
        }

        private sealed class ClassRoomPreference
        {
            public int BuildingId { get; set; }
            public List<int> RoomIds { get; set; } = new List<int>();
        }

        private sealed class RoomAssignmentContainer
        {
            public List<ClassRoomAssignment> ClassAssignments { get; } = new List<ClassRoomAssignment>();
            public List<RoomEvent> RoomEvents { get; } = new List<RoomEvent>();
            public Dictionary<(int timeIndex, int roomId), List<RoomEvent>> EventLookup { get; set; } = new Dictionary<(int timeIndex, int roomId), List<RoomEvent>>();
        }

        private sealed class SlotTimeConflict
        {
            public int TimeIndex { get; set; }
            public List<int> SubjectIds { get; set; } = new List<int>();
            public List<int> ClassIds { get; set; } = new List<int>();
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
        /// <summary>
        /// 当固定考试时间不可行时,允许向前/向后探索的最大时间窗(分钟)。小于等于 0 表示不启用自动时间调整。
        /// </summary>
        public int TimeAdjustWindowMinutes { get; set; } = 180;
        /// <summary>
        /// 自动时间调整的步长(分钟)。每次扩展容差时按该步长递增。
        /// </summary>
        public int TimeAdjustStepMinutes { get; set; } = 30;
        /// <summary>
        /// 排考失败时输出的错误原因条目数量。
        /// </summary>
        public int FailureReasonTopN { get; set; } = 5;
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