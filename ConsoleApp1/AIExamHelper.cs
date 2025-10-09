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
            var result = new List<AIExamResult>();
            var error = new StringBuilder();

            if (model == null)
            {
                error.AppendLine("排考参数不能为空。");
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            if (model.ModelTimeList == null || model.ModelTimeList.Count == 0)
            {
                error.AppendLine("排考时间段不能为空。");
            }

            if (model.ModelRoomList == null || model.ModelRoomList.Count == 0)
            {
                error.AppendLine("考场信息不能为空。");
            }

            if (model.ModelClassList == null || model.ModelClassList.Count == 0)
            {
                error.AppendLine("班级信息不能为空。");
            }

            if (model.ModelSubjectList == null || model.ModelSubjectList.Count == 0)
            {
                error.AppendLine("考试科目信息不能为空。");
            }

            if (error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var config = model.Config ?? new AIExamConfig();
            var classes = model.ModelClassList.ToDictionary(x => x.ModelClassId);
            var rooms = model.ModelRoomList.ToDictionary(x => x.ModelRoomId);

            var sessions = model.ModelTimeList
                .Select((x, index) => SessionInfo.Create(x, index))
                .Where(x => x != null)
                .Cast<SessionInfo>()
                .OrderBy(x => x.Start)
                .ToList();

            if (sessions.Count == 0)
            {
                throw new ResponseException("没有有效的考试时间段。", ErrorCode.ParamError);
            }

            var subjectList = model.ModelSubjectList
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.Duration)
                .ThenBy(x => x.ModelSubjectId)
                .ToList();

            var roomSchedules = rooms.Keys.ToDictionary(x => x, _ => new List<RoomInterval>());
            var roomSessionUsage = rooms.Keys.ToDictionary(x => x, _ => new Dictionary<int, int>());
            var classSchedules = classes.Keys.ToDictionary(x => x, _ => new List<ClassInterval>());
            var classDailyUsage = classes.Keys.ToDictionary(x => x, _ => new Dictionary<DateTime, int>());

            var subjectStartMap = new Dictionary<int, DateTime>();
            var jointRules = BuildJointRules(model.RuleJointSubjectList);
            var jointNotRules = BuildJointNotRules(model.RuleJointSubjectNotList);

            foreach (var subject in subjectList)
            {
                if (subject.ModelSubjectClassList == null || subject.ModelSubjectClassList.Count == 0)
                {
                    continue;
                }

                var subjectClasses = subject.ModelSubjectClassList
                    .Select(x => classes.TryGetValue(x.ModelClassId, out var cls) ? cls : null)
                    .Where(x => x != null)
                    .Cast<AIExamModelClass>()
                    .OrderBy(x => x.Grade)
                    .ThenBy(x => x.ModelClassId)
                    .ToList();

                if (subjectClasses.Count == 0)
                {
                    continue;
                }

                if (!TryScheduleSubject(
                        subject,
                        subjectClasses,
                        sessions,
                        rooms,
                        config,
                        roomSchedules,
                        roomSessionUsage,
                        classSchedules,
                        classDailyUsage,
                        subjectStartMap,
                        jointRules,
                        jointNotRules,
                        out var subjectResults,
                        out var errorMessage))
                {
                    error.AppendLine(errorMessage);
                }
                else
                {
                    result.AddRange(subjectResults);
                }
            }

            if (error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.Fail);
            }

            return result
                .OrderBy(x => x.Date)
                .ThenBy(x => x.StartTime)
                .ThenBy(x => x.ModelSubjectId)
                .ThenBy(x => x.ModelClassId)
                .ThenBy(x => x.ModelRoomId)
                .ToList();
        }

        private static bool TryScheduleSubject(
            AIExamModelSubject subject,
            List<AIExamModelClass> subjectClasses,
            List<SessionInfo> sessions,
            Dictionary<int, AIExamModelRoom> rooms,
            AIExamConfig config,
            Dictionary<int, List<RoomInterval>> roomSchedules,
            Dictionary<int, Dictionary<int, int>> roomSessionUsage,
            Dictionary<int, List<ClassInterval>> classSchedules,
            Dictionary<int, Dictionary<DateTime, int>> classDailyUsage,
            Dictionary<int, DateTime> subjectStartMap,
            List<JointRule> jointRules,
            List<JointNotRule> jointNotRules,
            out List<AIExamResult> subjectResults,
            out string errorMessage)
        {
            subjectResults = new List<AIExamResult>();
            errorMessage = string.Empty;

            var duration = TimeSpan.FromMinutes(subject.Duration);
            var options = new AutoTimeAdjustOptions();

            var relevantJointRules = jointRules.Where(r => r.SubjectIds.Contains(subject.ModelSubjectId)).ToList();
            var relevantJointNotRules = jointNotRules.Where(r => r.SubjectIds.Contains(subject.ModelSubjectId)).ToList();

            var candidateSessions = GetCandidateSessions(subject, sessions, relevantJointRules);
            if (candidateSessions.Count == 0)
            {
                errorMessage = $"科目【{subject.ModelSubjectName ?? subject.ModelSubjectId.ToString()}】没有可用的考试场次。";
                return false;
            }

            var conflictCuts = new HashSet<string>();

            foreach (var sessionCandidate in candidateSessions)
            {
                var session = sessionCandidate.Session;
                var startCandidates = GetCandidateStartTimes(subject, sessionCandidate, duration, options);

                foreach (var candidateStart in startCandidates)
                {
                    var key = $"{subject.ModelSubjectId}_{session.Index}_{candidateStart:yyyyMMddHHmm}";
                    if (!conflictCuts.Add(key))
                    {
                        continue;
                    }

                    var candidateEnd = candidateStart + duration;

                    if (!IsJointRuleSatisfied(candidateStart, subject.ModelSubjectId, relevantJointRules, subjectStartMap))
                    {
                        continue;
                    }

                    if (!IsJointNotRuleSatisfied(candidateStart, relevantJointNotRules, subjectStartMap))
                    {
                        continue;
                    }

                    if (!IsClassAvailabilitySatisfied(subjectClasses, candidateStart, candidateEnd, config, classSchedules, classDailyUsage, out var classValidationMessage))
                    {
                        continue;
                    }

                    if (!TryAllocateRooms(
                            subject,
                            subjectClasses,
                            candidateStart,
                            candidateEnd,
                            session,
                            rooms,
                            roomSchedules,
                            roomSessionUsage,
                            out var tempResults))
                    {
                        continue;
                    }

                    foreach (var cls in subjectClasses)
                    {
                        if (!classSchedules.TryGetValue(cls.ModelClassId, out var list))
                        {
                            list = new List<ClassInterval>();
                            classSchedules[cls.ModelClassId] = list;
                        }
                        list.Add(new ClassInterval(candidateStart, candidateEnd, subject.ModelSubjectId));

                        if (!classDailyUsage.TryGetValue(cls.ModelClassId, out var daily))
                        {
                            daily = new Dictionary<DateTime, int>();
                            classDailyUsage[cls.ModelClassId] = daily;
                        }

                        var day = candidateStart.Date;
                        if (!daily.ContainsKey(day))
                        {
                            daily[day] = 0;
                        }
                        daily[day]++;
                    }

                    subjectStartMap[subject.ModelSubjectId] = candidateStart;
                    subjectResults = tempResults;
                    return true;
                }
            }

            errorMessage = $"科目【{subject.ModelSubjectName ?? subject.ModelSubjectId.ToString()}】无法在可用时间段内安排考试。";
            return false;
        }


        private static bool TryAllocateRooms(
            AIExamModelSubject subject,
            List<AIExamModelClass> subjectClasses,
            DateTime start,
            DateTime end,
            SessionInfo session,
            Dictionary<int, AIExamModelRoom> rooms,
            Dictionary<int, List<RoomInterval>> roomSchedules,
            Dictionary<int, Dictionary<int, int>> roomSessionUsage,
            out List<AIExamResult> results)
        {
            results = new List<AIExamResult>();

            var availableRooms = rooms.Values
                .Where(r => string.IsNullOrWhiteSpace(subject.ExamMode) || string.Equals(r.ExamMode, subject.ExamMode, StringComparison.OrdinalIgnoreCase))
                .Where(r => IsRoomAvailable(r.ModelRoomId, start, end, session, roomSchedules, roomSessionUsage))
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.SeatCount)
                .ThenBy(r => r.RoomNo ?? int.MaxValue)
                .ToList();

            if (availableRooms.Count == 0)
            {
                return false;
            }

            var model = new CpModel();
            var classCandidates = new Dictionary<int, List<RoomSequenceCandidate>>();
            var assignmentVars = new Dictionary<(int ClassId, int CandidateIndex), BoolVar>();
            var roomUsageTerms = new Dictionary<int, List<(BoolVar Var, int StudentCount)>>();
            var roomGradeAssignments = new Dictionary<(int RoomId, int Grade), List<BoolVar>>();

            foreach (var cls in subjectClasses)
            {
                var candidates = BuildClassRoomCandidates(cls, availableRooms);
                if (candidates.Count == 0)
                {
                    return false;
                }

                classCandidates[cls.ModelClassId] = candidates;

                var classVars = new List<BoolVar>();

                for (var i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    var varName = $"assign_c{cls.ModelClassId}_cand{i}";
                    var assignVar = model.NewBoolVar(varName);
                    assignmentVars[(cls.ModelClassId, i)] = assignVar;
                    classVars.Add(assignVar);

                    foreach (var seat in candidate.Seats)
                    {
                        if (!roomUsageTerms.TryGetValue(seat.RoomId, out var usageList))
                        {
                            usageList = new List<(BoolVar Var, int StudentCount)>();
                            roomUsageTerms[seat.RoomId] = usageList;
                        }

                        usageList.Add((assignVar, seat.SeatCount));

                        var gradeKey = (seat.RoomId, cls.Grade);
                        if (!roomGradeAssignments.TryGetValue(gradeKey, out var gradeList))
                        {
                            gradeList = new List<BoolVar>();
                            roomGradeAssignments[gradeKey] = gradeList;
                        }

                        gradeList.Add(assignVar);
                    }
                }

                model.Add(LinearExpr.Sum(classVars) == 1);
            }

            var wasteVars = new List<IntVar>();

            foreach (var room in availableRooms)
            {
                if (!roomUsageTerms.TryGetValue(room.ModelRoomId, out var usages))
                {
                    continue;
                }

                var seatUsage = model.NewIntVar(0, room.SeatCount, $"seat_usage_room_{room.ModelRoomId}");
                var seatTerms = usages.Select(u => LinearExpr.Term(u.Var, u.StudentCount)).ToList();
                model.Add(seatUsage == LinearExpr.Sum(seatTerms));

                var roomUsed = model.NewBoolVar($"room_used_{room.ModelRoomId}");
                model.Add(seatUsage <= room.SeatCount * roomUsed);
                model.Add(seatUsage >= roomUsed);

                var wasteVar = model.NewIntVar(0, room.SeatCount, $"waste_room_{room.ModelRoomId}");
                model.Add(seatUsage + wasteVar == room.SeatCount * roomUsed);
                wasteVars.Add(wasteVar);

                var gradeVars = new List<BoolVar>();
                var gradesInRoom = roomGradeAssignments
                    .Where(kvp => kvp.Key.RoomId == room.ModelRoomId)
                    .Select(kvp => (Grade: kvp.Key.Grade, Vars: kvp.Value))
                    .ToList();

                foreach (var (grade, gradeVarList) in gradesInRoom)
                {
                    if (gradeVarList.Count == 0)
                    {
                        continue;
                    }

                    model.Add(LinearExpr.Sum(gradeVarList) <= 1);

                    var gradeIndicator = model.NewBoolVar($"room_{room.ModelRoomId}_grade_{grade}");
                    foreach (var assignVar in gradeVarList)
                    {
                        model.Add(assignVar <= gradeIndicator);
                    }

                    model.Add(gradeIndicator <= LinearExpr.Sum(gradeVarList));
                    gradeVars.Add(gradeIndicator);
                }

                if (gradeVars.Count > 0)
                {
                    model.Add(LinearExpr.Sum(gradeVars) <= 2);
                }
            }

            if (wasteVars.Count > 0)
            {
                model.Minimize(LinearExpr.Sum(wasteVars));
            }

            var solver = new CpSolver
            {
                StringParameters = "max_time_in_seconds:30,num_search_workers:8"
            };

            var status = solver.Solve(model);
            if (status != CpSolverStatus.Feasible && status != CpSolverStatus.Optimal)
            {
                return false;
            }

            var allocation = new Dictionary<int, List<RoomSeatAllocation>>();

            foreach (var cls in subjectClasses)
            {
                if (!classCandidates.TryGetValue(cls.ModelClassId, out var candidates))
                {
                    return false;
                }

                RoomSequenceCandidate? selected = null;
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (solver.Value(assignmentVars[(cls.ModelClassId, i)]) > 0.5)
                    {
                        selected = candidates[i];
                        break;
                    }
                }

                if (selected == null)
                {
                    return false;
                }

                allocation[cls.ModelClassId] = selected.Seats
                    .Select(seat => new RoomSeatAllocation
                    {
                        ClassId = cls.ModelClassId,
                        Grade = cls.Grade,
                        RoomId = seat.RoomId,
                        RoomNo = seat.RoomNo,
                        BuildingId = seat.BuildingId,
                        SeatCount = seat.SeatCount,
                        RoomSeat = seat.RoomSeat
                    })
                    .ToList();
            }

            results = BuildResults(subject, start, end, allocation);

            foreach (var classAllocation in allocation)
            {
                foreach (var seat in classAllocation.Value)
                {
                    if (!roomSchedules.TryGetValue(seat.RoomId, out var schedule))
                    {
                        schedule = new List<RoomInterval>();
                        roomSchedules[seat.RoomId] = schedule;
                    }

                    schedule.Add(new RoomInterval(start, end, session.Index, seat.Grade));

                    if (!roomSessionUsage.TryGetValue(seat.RoomId, out var sessionUsage))
                    {
                        sessionUsage = new Dictionary<int, int>();
                        roomSessionUsage[seat.RoomId] = sessionUsage;
                    }

                    if (!sessionUsage.ContainsKey(session.Index))
                    {
                        sessionUsage[session.Index] = 0;
                    }

                    sessionUsage[session.Index]++;
                }
            }

            return true;
        }

        private static List<RoomSequenceCandidate> BuildClassRoomCandidates(AIExamModelClass cls, List<AIExamModelRoom> rooms)
        {
            const int MaxRoomsPerClass = 6;
            var candidates = new List<RoomSequenceCandidate>();
            var seen = new HashSet<string>();

            foreach (var buildingGroup in rooms.GroupBy(r => r.BuildingId))
            {
                var ordered = buildingGroup
                    .Where(r => r.SeatCount > 0)
                    .OrderBy(r => r.RoomNo ?? int.MaxValue)
                    .ThenBy(r => r.ModelRoomId)
                    .ToList();

                for (var i = 0; i < ordered.Count; i++)
                {
                    var sequence = new List<AIExamModelRoom>();
                    var totalCapacity = 0;

                    for (var j = i; j < ordered.Count && sequence.Count < MaxRoomsPerClass; j++)
                    {
                        var room = ordered[j];
                        sequence.Add(room);
                        totalCapacity += room.SeatCount;

                        if (totalCapacity < cls.StudentCount)
                        {
                            continue;
                        }

                        if (sequence.Count > 1)
                        {
                            var previousCapacity = totalCapacity - room.SeatCount;
                            if (previousCapacity >= cls.StudentCount)
                            {
                                continue;
                            }
                        }

                        var distribution = DistributeStudentsEvenly(cls.StudentCount, sequence);
                        if (distribution == null)
                        {
                            continue;
                        }

                        var key = string.Join("-", sequence.Select(r => r.ModelRoomId));
                        if (!seen.Add(key))
                        {
                            continue;
                        }

                        var seats = sequence
                            .Select(roomCandidate => new RoomSeatAllocation
                            {
                                ClassId = cls.ModelClassId,
                                Grade = cls.Grade,
                                RoomId = roomCandidate.ModelRoomId,
                                RoomNo = roomCandidate.RoomNo,
                                BuildingId = roomCandidate.BuildingId,
                                SeatCount = distribution.TryGetValue(roomCandidate.ModelRoomId, out var value) ? value : 0,
                                RoomSeat = roomCandidate.SeatCount
                            })
                            .Where(x => x.SeatCount > 0)
                            .ToList();

                        if (seats.Sum(x => x.SeatCount) != cls.StudentCount)
                        {
                            continue;
                        }

                        candidates.Add(new RoomSequenceCandidate(seats));
                    }
                }
            }

            return candidates
                .OrderBy(c => c.Seats.Count)
                .ThenBy(c => c.TotalCapacity - cls.StudentCount)
                .ThenBy(c => c.Seats.Sum(s => s.RoomNo ?? int.MaxValue))
                .ThenBy(c => c.Seats.Sum(s => s.RoomId))
                .ToList();
        }

        private static Dictionary<int, int>? DistributeStudentsEvenly(int studentCount, List<AIExamModelRoom> sequence)
        {
            if (studentCount <= 0 || sequence.Count == 0)
            {
                return null;
            }

            var distribution = new Dictionary<int, int>();
            var suffixCapacity = new int[sequence.Count];
            var running = 0;
            for (var index = sequence.Count - 1; index >= 0; index--)
            {
                running += sequence[index].SeatCount;
                suffixCapacity[index] = running;
            }

            var remaining = studentCount;
            for (var i = 0; i < sequence.Count; i++)
            {
                var room = sequence[i];
                var roomsLeft = sequence.Count - i;
                var restCapacity = suffixCapacity[i] - room.SeatCount;
                var lowerBound = Math.Max(0, remaining - restCapacity);
                var upperBound = Math.Min(room.SeatCount, remaining);

                if (upperBound < lowerBound)
                {
                    return null;
                }

                var evenTarget = (int)Math.Ceiling((double)remaining / roomsLeft);
                var assign = Math.Min(upperBound, Math.Max(lowerBound, evenTarget));
                if (assign < lowerBound)
                {
                    assign = lowerBound;
                }

                distribution[room.ModelRoomId] = assign;
                remaining -= assign;
            }

            if (remaining != 0)
            {
                return null;
            }

            if (distribution.Count > 1)
            {
                var roomLookup = sequence.ToDictionary(r => r.ModelRoomId, r => r);
                while (true)
                {
                    var ordered = distribution.OrderBy(kv => kv.Value).ToList();
                    if (ordered.Count <= 1)
                    {
                        break;
                    }

                    var minEntry = ordered.First();
                    var maxEntry = ordered.Last();

                    if (maxEntry.Value - minEntry.Value <= 1)
                    {
                        break;
                    }

                    if (!roomLookup.TryGetValue(minEntry.Key, out var minRoom) || !roomLookup.TryGetValue(maxEntry.Key, out var maxRoom))
                    {
                        break;
                    }

                    if (minEntry.Value + 1 > minRoom.SeatCount)
                    {
                        break;
                    }

                    if (maxEntry.Value <= 1)
                    {
                        break;
                    }

                    distribution[maxEntry.Key] -= 1;
                    distribution[minEntry.Key] += 1;
                }
            }

            return distribution;
        }

        private static List<AIExamResult> BuildResults(
            AIExamModelSubject subject,
            DateTime start,
            DateTime end,
            Dictionary<int, List<RoomSeatAllocation>> allocation)
        {
            var list = new List<AIExamResult>();

            foreach (var entry in allocation)
            {
                foreach (var seat in entry.Value)
                {
                    list.Add(new AIExamResult
                    {
                        ModelSubjectId = subject.ModelSubjectId,
                        ModelClassId = entry.Key,
                        ModelRoomId = seat.RoomId,
                        Duration = subject.Duration,
                        StudentCount = seat.SeatCount,
                        SeatCount = seat.RoomSeat,
                        Date = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        StartTime = start.ToString("HH:mm", CultureInfo.InvariantCulture),
                        EndTime = end.ToString("HH:mm", CultureInfo.InvariantCulture)
                    });
                }
            }

            return list;
        }

        private static bool IsRoomAvailable(
            int roomId,
            DateTime start,
            DateTime end,
            SessionInfo session,
            Dictionary<int, List<RoomInterval>> roomSchedules,
            Dictionary<int, Dictionary<int, int>> roomSessionUsage)
        {
            if (!roomSchedules.TryGetValue(roomId, out var schedule))
            {
                return true;
            }

            foreach (var interval in schedule)
            {
                if (IsOverlapping(interval.Start, interval.End, start, end))
                {
                    return false;
                }
            }

            var limit = GetSessionRoomLimit(session.TimeNo);
            if (!roomSessionUsage.TryGetValue(roomId, out var usage))
            {
                return true;
            }

            if (!usage.TryGetValue(session.Index, out var count))
            {
                count = 0;
            }

            return count < limit;
        }

        private static int GetSessionRoomLimit(string? timeNo)
        {
            return timeNo switch
            {
                "上午场" => 2,
                "下午场" => 3,
                "晚上场" => 1,
                _ => 1
            };
        }

        private static bool IsClassAvailabilitySatisfied(
            List<AIExamModelClass> classes,
            DateTime start,
            DateTime end,
            AIExamConfig config,
            Dictionary<int, List<ClassInterval>> classSchedules,
            Dictionary<int, Dictionary<DateTime, int>> classDailyUsage,
            out string message)
        {
            message = string.Empty;
            foreach (var cls in classes)
            {
                var day = start.Date;
                if (config.MaxStudentDaily > 0)
                {
                    if (classDailyUsage.TryGetValue(cls.ModelClassId, out var daily) &&
                        daily.TryGetValue(day, out var count) &&
                        count >= config.MaxStudentDaily)
                    {
                        message = $"班级【{cls.ModelClassName ?? cls.ModelClassId.ToString()}】在{day:yyyy-MM-dd}考试次数超过限制。";
                        return false;
                    }
                }

                if (classSchedules.TryGetValue(cls.ModelClassId, out var intervals))
                {
                    foreach (var interval in intervals)
                    {
                        if (IsOverlapping(interval.Start, interval.End, start, end))
                        {
                            message = $"班级【{cls.ModelClassName ?? cls.ModelClassId.ToString()}】考试时间冲突。";
                            return false;
                        }

                        var minInterval = TimeSpan.FromMinutes(config.MinExamInterval);
                        if (interval.End <= start)
                        {
                            var gap = start - interval.End;
                            if (gap < minInterval)
                            {
                                message = $"班级【{cls.ModelClassName ?? cls.ModelClassId.ToString()}】考试间隔不足。";
                                return false;
                            }
                        }
                        else if (end <= interval.Start)
                        {
                            var gap = interval.Start - end;
                            if (gap < minInterval)
                            {
                                message = $"班级【{cls.ModelClassName ?? cls.ModelClassId.ToString()}】考试间隔不足。";
                                return false;
                            }
                        }
                    }
                }
            }

            return true;
        }

        private static bool IsOverlapping(DateTime start1, DateTime end1, DateTime start2, DateTime end2)
        {
            return start1 < end2 && start2 < end1;
        }

        private static List<SessionCandidate> GetCandidateSessions(
            AIExamModelSubject subject,
            List<SessionInfo> sessions,
            List<JointRule> jointRules)
        {
            var list = new List<SessionCandidate>();
            DateTime? jointFixedStart = null;

            foreach (var rule in jointRules)
            {
                if (rule.FixedStart.HasValue)
                {
                    jointFixedStart = rule.FixedStart;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(subject.Date) && !string.IsNullOrWhiteSpace(subject.StartTime))
            {
                var presetStart = ParseDateTime(subject.Date, subject.StartTime);
                var session = sessions.FirstOrDefault(s => s.Start <= presetStart && presetStart + TimeSpan.FromMinutes(subject.Duration) <= s.End);
                if (session != null)
                {
                    list.Add(new SessionCandidate(session, presetStart));
                    return list;
                }
            }

            if (jointFixedStart.HasValue)
            {
                var session = sessions.FirstOrDefault(s => s.Start <= jointFixedStart && jointFixedStart.Value <= s.End);
                if (session != null)
                {
                    list.Add(new SessionCandidate(session, jointFixedStart));
                }
                return list;
            }

            foreach (var session in sessions)
            {
                list.Add(new SessionCandidate(session, null));
            }

            return list;
        }

        private static List<DateTime> GetCandidateStartTimes(
            AIExamModelSubject subject,
            SessionCandidate sessionCandidate,
            TimeSpan duration,
            AutoTimeAdjustOptions options)
        {
            var list = new List<DateTime>();
            if (sessionCandidate.PresetStart.HasValue)
            {
                var preset = sessionCandidate.PresetStart.Value;
                if (preset >= sessionCandidate.Session.Start && preset + duration <= sessionCandidate.Session.End)
                {
                    list.Add(preset);
                }
                return list;
            }

            var start = sessionCandidate.Session.Start;
            var end = sessionCandidate.Session.End - duration;

            if (end < start)
            {
                return list;
            }

            var current = start;
            while (current <= end && list.Count < options.MaxAttemptsPerSession)
            {
                list.Add(current);
                current = current.AddMinutes(options.StepMinutes);
            }

            return list;
        }

        private static bool IsJointRuleSatisfied(DateTime candidateStart, int subjectId, List<JointRule> jointRules, Dictionary<int, DateTime> subjectStartMap)
        {
            foreach (var rule in jointRules)
            {
                if (rule.FixedStart.HasValue && rule.FixedStart.Value != candidateStart)
                {
                    return false;
                }

                foreach (var other in rule.SubjectIds)
                {
                    if (other == subjectId)
                    {
                        continue;
                    }

                    if (subjectStartMap.TryGetValue(other, out var start))
                    {
                        if (start != candidateStart)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private static bool IsJointNotRuleSatisfied(DateTime candidateStart, List<JointNotRule> jointRules, Dictionary<int, DateTime> subjectStartMap)
        {
            foreach (var rule in jointRules)
            {
                foreach (var other in rule.SubjectIds)
                {
                    if (subjectStartMap.TryGetValue(other, out var start) && start == candidateStart)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static DateTime ParseDateTime(string date, string time)
        {
            var dateTimeString = $"{date} {time}";
            return DateTime.Parse(dateTimeString, CultureInfo.InvariantCulture);
        }

        private static List<JointRule> BuildJointRules(List<AIExamRuleJointSubject>? rules)
        {
            var list = new List<JointRule>();
            if (rules == null)
            {
                return list;
            }

            foreach (var rule in rules)
            {
                if (rule.RuleJointSubjectList == null || rule.RuleJointSubjectList.Count == 0)
                {
                    continue;
                }

                DateTime? fixedStart = null;
                if (!string.IsNullOrWhiteSpace(rule.Date) && !string.IsNullOrWhiteSpace(rule.StartTime))
                {
                    fixedStart = ParseDateTime(rule.Date, rule.StartTime);
                }

                list.Add(new JointRule
                {
                    SubjectIds = rule.RuleJointSubjectList.Select(x => x.ModelSubjectId).ToHashSet(),
                    FixedStart = fixedStart
                });
            }

            return list;
        }

        private static List<JointNotRule> BuildJointNotRules(List<AIExamRuleJointSubjectNot>? rules)
        {
            var list = new List<JointNotRule>();
            if (rules == null)
            {
                return list;
            }

            foreach (var rule in rules)
            {
                if (rule.RuleJointSubjectList == null || rule.RuleJointSubjectList.Count == 0)
                {
                    continue;
                }

                list.Add(new JointNotRule
                {
                    SubjectIds = rule.RuleJointSubjectList.Select(x => x.ModelSubjectId).ToHashSet()
                });
            }

            return list;
        }

        private class SessionInfo
        {
            public int Index { get; private set; }
            public string? Date { get; private set; }
            public string? TimeNo { get; private set; }
            public DateTime Start { get; private set; }
            public DateTime End { get; private set; }

            public static SessionInfo? Create(AIExamModelTime model, int index)
            {
                if (model == null || string.IsNullOrWhiteSpace(model.Date) || string.IsNullOrWhiteSpace(model.StartTime) || string.IsNullOrWhiteSpace(model.EndTime))
                {
                    return null;
                }

                var start = ParseDateTime(model.Date, model.StartTime);
                var end = ParseDateTime(model.Date, model.EndTime);
                if (end <= start)
                {
                    return null;
                }

                return new SessionInfo
                {
                    Index = index,
                    Date = model.Date,
                    TimeNo = model.TimeNo,
                    Start = start,
                    End = end
                };
            }
        }

        private class SessionCandidate
        {
            public SessionCandidate(SessionInfo session, DateTime? presetStart)
            {
                Session = session;
                PresetStart = presetStart;
            }

            public SessionInfo Session { get; }
            public DateTime? PresetStart { get; }
        }

        private class RoomInterval
        {
            public RoomInterval(DateTime start, DateTime end, int sessionIndex, int grade)
            {
                Start = start;
                End = end;
                SessionIndex = sessionIndex;
                Grade = grade;
            }

            public DateTime Start { get; }
            public DateTime End { get; }
            public int SessionIndex { get; }
            public int Grade { get; }
        }

        private class ClassInterval
        {
            public ClassInterval(DateTime start, DateTime end, int subjectId)
            {
                Start = start;
                End = end;
                SubjectId = subjectId;
            }

            public DateTime Start { get; }
            public DateTime End { get; }
            public int SubjectId { get; }
        }

        private class RoomSequenceCandidate
        {
            public RoomSequenceCandidate(List<RoomSeatAllocation> seats)
            {
                Seats = seats;
                TotalCapacity = seats.Sum(x => x.RoomSeat);
            }

            public List<RoomSeatAllocation> Seats { get; }
            public int TotalCapacity { get; }
        }

        private class RoomSeatAllocation
        {
            public int ClassId { get; set; }
            public int Grade { get; set; }
            public int RoomId { get; set; }
            public int? RoomNo { get; set; }
            public int BuildingId { get; set; }
            public int SeatCount { get; set; }
            public int RoomSeat { get; set; }
        }

        private class JointRule
        {
            public HashSet<int> SubjectIds { get; set; } = new HashSet<int>();
            public DateTime? FixedStart { get; set; }
        }

        private class JointNotRule
        {
            public HashSet<int> SubjectIds { get; set; } = new HashSet<int>();
        }

        private class AutoTimeAdjustOptions
        {
            public bool Enabled { get; set; } = true;
            public int StepMinutes { get; set; } = 10;
            public int MaxAttemptsPerSession { get; set; } = 48;
            public bool AllowCrossSession { get; set; } = true;
        }
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
