using DTcms.Core.Common.Emums;
using DTcms.Core.Common.Extensions;
using Google.OrTools.Sat;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace DTcms.Core.Common.Helpers
{
    /// <summary>
    /// 排考帮助类
    /// </summary>
    public static class AIExamHelper
    {
        private const int MaxGradesPerRoom = 2;

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

            var subjects = model.ModelSubjectList ?? new List<AIExamModelSubject>();
            var classes = model.ModelClassList ?? new List<AIExamModelClass>();
            var rooms = model.ModelRoomList ?? new List<AIExamModelRoom>();
            var times = model.ModelTimeList ?? new List<AIExamModelTime>();

            if (subjects.Count == 0)
            {
                error.AppendLine("缺少考试科目信息，无法排考。");
            }

            if (classes.Count == 0)
            {
                error.AppendLine("缺少班级信息，无法排考。");
            }

            if (rooms.Count == 0)
            {
                error.AppendLine("缺少考场信息，无法排考。");
            }

            if (times.Count == 0)
            {
                error.AppendLine("缺少考试场次信息，无法排考。");
            }

            if (error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var config = model.Config ?? new AIExamConfig();
            var minGap = Math.Max(0, config.MinExamInterval);
            var maxDaily = config.MaxStudentDaily <= 0 ? int.MaxValue : config.MaxStudentDaily;

            var classLookup = classes.ToDictionary(x => x.ModelClassId);
            var roomLookup = rooms.ToDictionary(x => x.ModelRoomId);
            var totalSeatCapacity = rooms.Sum(r => Math.Max(0, r.SeatCount));

            if (totalSeatCapacity <= 0)
            {
                error.AppendLine("考场总可用座位数为0，无法排考。");
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var sessions = BuildSessions(times, error);
            if (sessions.Count == 0)
            {
                error.AppendLine("考试场次时间配置无效，无法排考。");
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var classRoomPlanner = BuildClassRoomPlans(classes, rooms, error);
            if (classRoomPlanner == null || error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }
            var subjectClassMap = BuildSubjectClassMap(subjects, classLookup, error);
            if (error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var forcedDateMap = BuildForcedDateMap(model);
            var forcedStartMap = BuildForcedStartMap(model);
            var whitelistRooms = BuildRoomRuleLookup(model.RuleRoomSubjectList);
            var blacklistRooms = BuildRoomRuleLookup(model.RuleRoomSubjectNotList);

            var jointGroups = BuildJointSubjectGroups(model.RuleJointSubjectList);
            var jointNotMap = BuildJointSubjectNotMap(model.RuleJointSubjectNotList);
            var subjectToGroup = BuildSubjectToGroupMap(jointGroups);

            var baseCandidateSessionMap = BuildCandidateSessionMap(subjects, sessions, forcedDateMap, error);
            if (error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var sessionExclusions = new Dictionary<int, HashSet<string>>();
            var exclusionHistory = new Stack<(int SubjectId, string SessionKey)>();
            Dictionary<int, List<SessionOption>> filteredCandidateMap = baseCandidateSessionMap;
            Dictionary<int, int>? solverSelection = null;
            Dictionary<int, SubjectSchedule>? subjectSchedules = null;
            SessionReassignmentRequest? retryRequest = null;
            var maxSessionAttempts = Math.Max(1, sessions.Count * Math.Max(1, subjects.Count) + 5);
            string? lastPlannerFailure = null;

            for (var attempt = 0; attempt < maxSessionAttempts; attempt++)
            {
                filteredCandidateMap = FilterCandidateSessionMap(baseCandidateSessionMap, sessionExclusions);

                if (ValidateCandidateSessions(subjects, filteredCandidateMap, error))
                {
                    throw new ResponseException(error.ToString(), ErrorCode.ParamError);
                }

                retryRequest = null;

                solverSelection = SolveSubjectSessions(
                    subjects,
                    subjectClassMap,
                    filteredCandidateMap,
                    jointGroups,
                    jointNotMap,
                    maxDaily,
                    totalSeatCapacity,
                    error);

                if (solverSelection == null)
                {
                    if (TryBacktrackSessionExclusion(sessionExclusions, exclusionHistory))
                    {
                        continue;
                    }

                    if (error.Length == 0)
                    {
                        error.AppendLine("Google OR-Tools 未能找到满足所有约束的考试时间安排，请检查排考配置。");
                    }

                    throw new ResponseException(error.ToString(), ErrorCode.ParamError);
                }

                subjectSchedules = BuildSessionTimetable(
                    solverSelection,
                    subjects,
                    filteredCandidateMap,
                    sessions,
                    subjectClassMap,
                    classRoomPlanner,
                    subjectToGroup,
                    forcedStartMap,
                    whitelistRooms,
                    blacklistRooms,
                    roomLookup,
                    minGap,
                    error,
                    out retryRequest);

                if (retryRequest != null && string.IsNullOrWhiteSpace(retryRequest.Reason))
                {
                    retryRequest = new SessionReassignmentRequest(
                        retryRequest.Session,
                        retryRequest.SubjectIds,
                        $"场次[{retryRequest.Session.TimeNo}]无法完成考场安排。");
                }

                if (retryRequest != null && !string.IsNullOrWhiteSpace(retryRequest.Reason))
                {
                    lastPlannerFailure = retryRequest.Reason;
                }

                if (subjectSchedules != null)
                {
                    break;
                }

                if (retryRequest != null && retryRequest.SubjectIds.Count == 0 && solverSelection != null)
                {
                    var fallbackSubjectIds = solverSelection
                        .Where(kvp =>
                            filteredCandidateMap.TryGetValue(kvp.Key, out var options)
                                && kvp.Value >= 0
                                && kvp.Value < options.Count
                                && options[kvp.Value].Session.SessionKey == retryRequest.Session.SessionKey)
                        .Select(kvp => kvp.Key)
                        .Distinct()
                        .ToList();

                    if (fallbackSubjectIds.Count > 0)
                    {
                        retryRequest = new SessionReassignmentRequest(
                            retryRequest.Session,
                            fallbackSubjectIds,
                            retryRequest.Reason);
                    }
                }

                if (retryRequest == null || retryRequest.SubjectIds.Count == 0)
                {
                    if (retryRequest != null && string.IsNullOrWhiteSpace(retryRequest.Reason) && !string.IsNullOrWhiteSpace(lastPlannerFailure))
                    {
                        error.AppendLine(lastPlannerFailure);
                    }

                    if (error.Length == 0 && retryRequest != null)
                    {
                        error.AppendLine($"场次[{retryRequest.Session.TimeNo}]无法完成考场安排。");
                    }

                    throw new ResponseException(error.ToString(), ErrorCode.ParamError);
                }

                var excluded = false;
                foreach (var subjectId in retryRequest.SubjectIds)
                {
                    if (!baseCandidateSessionMap.TryGetValue(subjectId, out var baseOptions) || baseOptions.Count == 0)
                    {
                        continue;
                    }

                    if (baseOptions.All(opt => opt.Session.SessionKey == retryRequest.Session.SessionKey))
                    {
                        continue;
                    }

                    if (!sessionExclusions.TryGetValue(subjectId, out var set))
                    {
                        set = new HashSet<string>();
                        sessionExclusions[subjectId] = set;
                    }

                    if (set.Add(retryRequest.Session.SessionKey))
                    {
                        excluded = true;
                        exclusionHistory.Push((subjectId, retryRequest.Session.SessionKey));
                    }
                }

                if (!excluded)
                {
                    if (!string.IsNullOrWhiteSpace(retryRequest.Reason))
                    {
                        error.AppendLine(retryRequest.Reason);
                    }
                    else
                    {
                        error.AppendLine($"场次[{retryRequest.Session.TimeNo}]无法完成考场安排。");
                    }

                    throw new ResponseException(error.ToString(), ErrorCode.ParamError);
                }
            }

            if (subjectSchedules == null)
            {
                if (retryRequest != null)
                {
                    if (string.IsNullOrWhiteSpace(retryRequest.Reason) && !string.IsNullOrWhiteSpace(lastPlannerFailure))
                    {
                        error.AppendLine(lastPlannerFailure);
                    }
                    else if (!string.IsNullOrWhiteSpace(retryRequest.Reason))
                    {
                        error.AppendLine(retryRequest.Reason);
                    }
                    else
                    {
                        error.AppendLine($"场次[{retryRequest.Session.TimeNo}]无法完成考场安排。");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(lastPlannerFailure))
                {
                    error.AppendLine(lastPlannerFailure);
                }

                if (error.Length == 0)
                {
                    error.AppendLine("无法完成考试时间安排。");
                }

                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var results = BuildExamResults(
                subjects,
                subjectClassMap,
                classRoomPlanner,
                subjectSchedules,
                roomLookup,
                whitelistRooms,
                blacklistRooms,
                error);

            if (error.Length > 0)
            {
                throw new ResponseException(error.ToString(), ErrorCode.ParamError);
            }

            var orderedResults = results
                .OrderBy(r => r.Date)
                .ThenBy(r => r.StartTime)
                .ThenBy(r => r.ModelRoomId)
                .ThenBy(r => r.ModelClassId)
                .ToList();

            AssignInvigilators(orderedResults, model, roomLookup);

            return orderedResults;
        }

        #region 房间与班级分配

        private static List<ExamSession> BuildSessions(List<AIExamModelTime> times, StringBuilder error)
        {
            var sessions = new List<ExamSession>();
            if (times == null)
            {
                return sessions;
            }

            var grouped = times
                .Where(t => !string.IsNullOrWhiteSpace(t.Date)
                    && !string.IsNullOrWhiteSpace(t.TimeNo)
                    && !string.IsNullOrWhiteSpace(t.StartTime)
                    && !string.IsNullOrWhiteSpace(t.EndTime))
                .GroupBy(t => new { Date = t.Date!.Trim(), TimeNo = t.TimeNo!.Trim() });

            foreach (var group in grouped)
            {
                var intervals = group
                    .Select(t => new
                    {
                        Start = ParseDateTime(group.Key.Date, t.StartTime),
                        End = ParseDateTime(group.Key.Date, t.EndTime)
                    })
                    .Where(x => x.Start.HasValue && x.End.HasValue && x.Start < x.End)
                    .ToList();

                if (intervals.Count == 0)
                {
                    error.AppendLine($"考试场次[{group.Key.Date} {group.Key.TimeNo}]的时间配置无效。");
                    continue;
                }

                var start = intervals.Min(x => x.Start)!.Value;
                var end = intervals.Max(x => x.End)!.Value;
                sessions.Add(new ExamSession(group.Key.Date, group.Key.TimeNo, start, end, 0));
            }

            sessions = sessions
                .OrderBy(s => s.Start)
                .ThenBy(s => s.End)
                .ToList();

            for (var i = 0; i < sessions.Count; i++)
            {
                sessions[i] = new ExamSession(sessions[i].DateText, sessions[i].TimeNo, sessions[i].Start, sessions[i].End, i);
            }

            return sessions;
        }

        private static ClassRoomPlanner BuildClassRoomPlans(
            List<AIExamModelClass> classes,
            List<AIExamModelRoom> rooms,
            StringBuilder error)
        {
            return ClassRoomPlanner.Build(classes, rooms, error);
        }

        private static List<ClassRoomSlice>? AllocateRoomsForClass(
            AIExamModelClass cls,
            List<BuildingRooms> buildings,
            HashSet<int> usedRooms,
            HashSet<int>? avoidRooms = null)
        {
            var needed = cls.StudentCount;
            if (needed <= 0)
            {
                return new List<ClassRoomSlice>();
            }

            var candidates = new List<(List<RoomSnapshot> Rooms, List<int> Distribution, int Waste, int Adjacency)>();
            var seen = new HashSet<string>();

            void TryAddCandidate(List<RoomSnapshot>? rooms)
            {
                if (rooms == null || rooms.Count == 0)
                {
                    return;
                }

                var ordered = rooms
                    .Where(r => r.AvailableSeats > 0
                        && !usedRooms.Contains(r.RoomId)
                        && (avoidRooms == null || !avoidRooms.Contains(r.RoomId)))
                    .OrderByDescending(r => r.AvailableSeats)
                    .ThenBy(r => r.RoomId)
                    .ToList();

                if (ordered.Count == 0)
                {
                    return;
                }

                var key = string.Join("_", ordered.Select(r => r.RoomId));
                if (!seen.Add(key))
                {
                    return;
                }

                var distribution = DistributeStudents(needed, ordered.Select(r => r.AvailableSeats).ToList());
                if (distribution == null)
                {
                    return;
                }

                var waste = ordered.Sum(r => r.AvailableSeats) - needed;
                var adjacency = CalculateAdjacencyScore(ordered);
                candidates.Add((ordered, distribution, waste, adjacency));
            }

            foreach (var building in buildings)
            {
                var available = building.Rooms
                    .Where(r => !usedRooms.Contains(r.RoomId)
                        && (avoidRooms == null || !avoidRooms.Contains(r.RoomId)))
                    .ToList();

                if (available.Count == 0)
                {
                    continue;
                }

                TryAddCandidate(FindBestRoomCombination(available, needed));
            }

            var globalAvailable = buildings
                .SelectMany(b => b.Rooms)
                .Where(r => r.AvailableSeats > 0
                    && !usedRooms.Contains(r.RoomId)
                    && (avoidRooms == null || !avoidRooms.Contains(r.RoomId)))
                .ToList();

            TryAddCandidate(FindBestRoomCombination(globalAvailable, needed));

            if (candidates.Count == 0)
            {
                return null;
            }

            var best = candidates
                .OrderBy(c => c.Rooms.Count)
                .ThenBy(c => c.Waste)
                .ThenBy(c => c.Adjacency)
                .ThenBy(c => c.Rooms.Min(r => r.RoomId))
                .First();

            var allocation = new List<ClassRoomSlice>();
            var selectedRoomIds = new HashSet<int>();

            for (var i = 0; i < best.Rooms.Count; i++)
            {
                var room = best.Rooms[i];
                var assigned = best.Distribution[i];
                var capacity = _roomLookup.TryGetValue(room.RoomId, out var info)
                    ? info.SeatCount
                    : room.Capacity;
                allocation.Add(new ClassRoomSlice(room.RoomId, assigned, capacity));
                usedRooms.Add(room.RoomId);
                selectedRoomIds.Add(room.RoomId);
            }

            foreach (var building in buildings)
            {
                building.Rooms.RemoveAll(r => selectedRoomIds.Contains(r.RoomId));
            }

            return allocation;
        }

        private static List<RoomSnapshot>? FindBestRoomCombination(List<RoomSnapshot> rooms, int needed)
        {
            if (rooms == null || rooms.Count == 0)
            {
                return null;
            }

            var ordered = rooms
                .OrderBy(r => r.AvailableSeats)
                .ThenBy(r => r.RoomId)
                .ToList();

            if (ordered.Count > 18)
            {
                var greedy = ordered
                    .OrderByDescending(r => r.AvailableSeats)
                    .ThenBy(r => r.RoomId)
                    .ToList();

                var selection = new List<RoomSnapshot>();
                var sum = 0;
                foreach (var room in greedy)
                {
                    selection.Add(room);
                    sum += room.AvailableSeats;
                    if (sum >= needed)
                    {
                        return selection;
                    }
                }

                return null;
            }

            var suffix = new int[ordered.Count + 1];
            for (var i = ordered.Count - 1; i >= 0; i--)
            {
                suffix[i] = suffix[i + 1] + ordered[i].AvailableSeats;
            }

            List<RoomSnapshot>? best = null;
            var bestCount = int.MaxValue;
            var bestWaste = int.MaxValue;
            var current = new List<RoomSnapshot>();

            void Search(int index, int sum)
            {
                if (sum >= needed)
                {
                    var waste = sum - needed;
                    if (current.Count < bestCount || (current.Count == bestCount && waste < bestWaste))
                    {
                        best = new List<RoomSnapshot>(current);
                        bestCount = current.Count;
                        bestWaste = waste;
                    }
                    return;
                }

                if (index >= ordered.Count)
                {
                    return;
                }

                if (current.Count >= bestCount)
                {
                    return;
                }

                if (sum + suffix[index] < needed)
                {
                    return;
                }

                for (var i = index; i < ordered.Count; i++)
                {
                    current.Add(ordered[i]);
                    Search(i + 1, sum + ordered[i].AvailableSeats);
                    current.RemoveAt(current.Count - 1);
                }
            }

            Search(0, 0);
            return best;
        }

        private static int CalculateAdjacencyScore(List<RoomSnapshot> rooms)
        {
            if (rooms == null || rooms.Count <= 1)
            {
                return 0;
            }

            var groups = rooms
                .GroupBy(r => r.BuildingId)
                .ToList();

            var penalty = (groups.Count - 1) * 1_000_000;

            foreach (var group in groups)
            {
                var numbers = group
                    .Select(r => r.RoomNo ?? int.MaxValue)
                    .OrderBy(n => n)
                    .ToList();

                if (numbers.Count == 0)
                {
                    continue;
                }

                if (numbers.Any(n => n == int.MaxValue))
                {
                    penalty += 100_000;
                    continue;
                }

                penalty += numbers[numbers.Count - 1] - numbers[0];

                for (var i = 1; i < numbers.Count; i++)
                {
                    penalty += Math.Abs(numbers[i] - numbers[i - 1]);
                }
            }

            return penalty;
        }

        private static List<int>? DistributeStudents(int total, List<int> capacities)
        {
            if (total <= 0 || capacities.Count == 0)
            {
                return null;
            }

            var totalCapacity = capacities.Sum();
            if (totalCapacity < total)
            {
                return null;
            }

            var remaining = total;
            var distribution = new int[capacities.Count];

            for (var i = 0; i < capacities.Count; i++)
            {
                var capacity = capacities[i];
                var roomsLeft = capacities.Count - i - 1;
                var minNeededForOthers = roomsLeft;
                var maxAssignable = Math.Min(capacity, remaining - minNeededForOthers);

                if (maxAssignable <= 0)
                {
                    return null;
                }

                distribution[i] = maxAssignable;
                remaining -= maxAssignable;
            }

            return remaining == 0 ? distribution.ToList() : null;
        }

        private static bool RoomSupportsGrade(
            int roomId,
            int grade,
            Dictionary<int, HashSet<int>> usage,
            Dictionary<int, int>? seatUsage = null,
            Dictionary<int, AIExamModelRoom>? roomLookup = null,
            int maxGradesPerRoom = MaxGradesPerRoom)
        {
            var hasCapacity = true;
            if (roomLookup != null && roomLookup.TryGetValue(roomId, out var room))
            {
                var used = seatUsage != null && seatUsage.TryGetValue(roomId, out var seatCount)
                    ? seatCount
                    : 0;
                hasCapacity = room.SeatCount - used > 0;
            }

            if (!hasCapacity)
            {
                return false;
            }

            if (!usage.TryGetValue(roomId, out var grades) || grades.Count == 0)
            {
                return true;
            }

            if (grades.Contains(grade))
            {
                return hasCapacity;
            }

            if (grades.Count >= maxGradesPerRoom)
            {
                return false;
            }

            return hasCapacity;
        }

        #endregion
        #region 科目与场次求解

        private static Dictionary<int, List<AIExamModelClass>> BuildSubjectClassMap(
            List<AIExamModelSubject> subjects,
            Dictionary<int, AIExamModelClass> classLookup,
            StringBuilder error)
        {
            var result = new Dictionary<int, List<AIExamModelClass>>();

            foreach (var subject in subjects)
            {
                if (subject.ModelSubjectClassList == null || subject.ModelSubjectClassList.Count == 0)
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]未关联任何班级。");
                    continue;
                }

                var classList = subject.ModelSubjectClassList
                    .Select(x => classLookup.TryGetValue(x.ModelClassId, out var cls) ? cls : null)
                    .Where(x => x != null)
                    .Distinct()
                    .Cast<AIExamModelClass>()
                    .ToList();

                if (classList.Count == 0)
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]关联的班级在班级列表中不存在。");
                    continue;
                }

                result[subject.ModelSubjectId] = classList;
            }

            return result;
        }

        private static Dictionary<int, List<SessionOption>> BuildCandidateSessionMap(
            List<AIExamModelSubject> subjects,
            List<ExamSession> sessions,
            Dictionary<int, string> forcedDateMap,
            StringBuilder error)
        {
            var map = new Dictionary<int, List<SessionOption>>();

            foreach (var subject in subjects)
            {
                var forcedDate = forcedDateMap.TryGetValue(subject.ModelSubjectId, out var date)
                    ? date
                    : subject.Date;

                var candidates = sessions
                    .Where(s => string.IsNullOrWhiteSpace(forcedDate) || string.Equals(s.DateText, forcedDate, StringComparison.Ordinal))
                    .Select(s => new SessionOption(s))
                    .ToList();

                if (candidates.Count == 0)
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]没有匹配的考试场次。");
                    continue;
                }

                map[subject.ModelSubjectId] = candidates;
            }

            return map;
        }

        private static Dictionary<int, List<SessionOption>> FilterCandidateSessionMap(
            Dictionary<int, List<SessionOption>> source,
            Dictionary<int, HashSet<string>> exclusions)
        {
            var result = new Dictionary<int, List<SessionOption>>(source.Count);

            foreach (var kvp in source)
            {
                if (exclusions.TryGetValue(kvp.Key, out var banned) && banned.Count > 0)
                {
                    var filtered = kvp.Value
                        .Where(option => !banned.Contains(option.Session.SessionKey))
                        .ToList();

                    if (filtered.Count == 0)
                    {
                        banned.Clear();
                        result[kvp.Key] = kvp.Value.ToList();
                    }
                    else
                    {
                        result[kvp.Key] = filtered;
                    }
                }
                else
                {
                    result[kvp.Key] = kvp.Value.ToList();
                }
            }

            return result;
        }

        private static bool TryBacktrackSessionExclusion(
            Dictionary<int, HashSet<string>> exclusions,
            Stack<(int SubjectId, string SessionKey)> history)
        {
            while (history.Count > 0)
            {
                var (subjectId, sessionKey) = history.Pop();
                if (!exclusions.TryGetValue(subjectId, out var banned) || banned.Count == 0)
                {
                    continue;
                }

                if (!banned.Remove(sessionKey))
                {
                    continue;
                }

                if (banned.Count == 0)
                {
                    exclusions.Remove(subjectId);
                }

                return true;
            }

            return false;
        }

        private static bool ValidateCandidateSessions(
            List<AIExamModelSubject> subjects,
            Dictionary<int, List<SessionOption>> candidateSessionMap,
            StringBuilder error)
        {
            foreach (var subject in subjects)
            {
                if (!candidateSessionMap.TryGetValue(subject.ModelSubjectId, out var options) || options.Count == 0)
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]没有可用的考试场次，无法排考。");
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<int, int>? SolveSubjectSessions(
            List<AIExamModelSubject> subjects,
            Dictionary<int, List<AIExamModelClass>> subjectClassMap,
            Dictionary<int, List<SessionOption>> candidateSessionMap,
            List<HashSet<int>> jointGroups,
            Dictionary<int, HashSet<int>> jointNotMap,
            int maxDaily,
            int totalSeatCapacity,
            StringBuilder error)
        {
            var model = new CpModel();
            var selectionVars = new Dictionary<(int subjectId, int optionIndex), BoolVar>();
            var subjectOptions = new Dictionary<int, List<BoolVar>>();
            var subjectLookup = subjects.ToDictionary(s => s.ModelSubjectId);
            var subjectSeatDemand = new Dictionary<int, int>();

            foreach (var subject in subjects)
            {
                if (!candidateSessionMap.TryGetValue(subject.ModelSubjectId, out var options) || options.Count == 0)
                {
                    continue;
                }

                var vars = new List<BoolVar>();
                for (var i = 0; i < options.Count; i++)
                {
                    var boolVar = model.NewBoolVar($"subject_{subject.ModelSubjectId}_session_{i}");
                    selectionVars[(subject.ModelSubjectId, i)] = boolVar;
                    vars.Add(boolVar);
                }

                model.Add(LinearExpr.Sum(vars) == 1);
                subjectOptions[subject.ModelSubjectId] = vars;

                if (subjectClassMap.TryGetValue(subject.ModelSubjectId, out var subjectClasses))
                {
                    subjectSeatDemand[subject.ModelSubjectId] = subjectClasses
                        .Sum(c => Math.Max(0, c.StudentCount));
                }
            }

            if (selectionVars.Count == 0)
            {
                return null;
            }

            foreach (var kvp in subjectSeatDemand)
            {
                if (kvp.Value > totalSeatCapacity && subjectLookup.TryGetValue(kvp.Key, out var subject))
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]的考生总数超过所有考场的总座位数，无法排考。");
                    return null;
                }
            }

            var classDayUsage = new Dictionary<(int ClassId, string Date), List<BoolVar>>();
            var classSessionUsage = new Dictionary<(int ClassId, string SessionKey), List<BoolVar>>();

            foreach (var kvp in subjectClassMap)
            {
                if (!subjectOptions.TryGetValue(kvp.Key, out var vars))
                {
                    continue;
                }

                var sessionsForSubject = candidateSessionMap[kvp.Key];
                for (var i = 0; i < sessionsForSubject.Count; i++)
                {
                    var option = sessionsForSubject[i];
                    var varRef = selectionVars[(kvp.Key, i)];

                    foreach (var cls in kvp.Value)
                    {
                        var dayKey = (cls.ModelClassId, option.Session.DateText);
                        if (!classDayUsage.TryGetValue(dayKey, out var dayList))
                        {
                            dayList = new List<BoolVar>();
                            classDayUsage[dayKey] = dayList;
                        }
                        dayList.Add(varRef);

                        var sessionKey = (cls.ModelClassId, option.Session.SessionKey);
                        if (!classSessionUsage.TryGetValue(sessionKey, out var sessionList))
                        {
                            sessionList = new List<BoolVar>();
                            classSessionUsage[sessionKey] = sessionList;
                        }
                        sessionList.Add(varRef);
                    }

                }
            }

            foreach (var list in classDayUsage.Values)
            {
                model.Add(LinearExpr.Sum(list) <= maxDaily);
            }

            foreach (var list in classSessionUsage.Values)
            {
                model.Add(LinearExpr.Sum(list) <= 1);
            }

            foreach (var group in jointGroups)
            {
                var subjectIds = group.Where(subjectOptions.ContainsKey).ToList();
                if (subjectIds.Count < 2)
                {
                    continue;
                }

                var baseId = subjectIds[0];
                var baseOptions = candidateSessionMap[baseId];
                for (var idx = 1; idx < subjectIds.Count; idx++)
                {
                    var otherId = subjectIds[idx];
                    var otherOptions = candidateSessionMap[otherId];

                    for (var i = 0; i < otherOptions.Count; i++)
                    {
                        var otherSession = otherOptions[i];
                        var matchIndex = baseOptions.FindIndex(opt => opt.Session.SessionKey == otherSession.Session.SessionKey);
                        if (matchIndex >= 0)
                        {
                            model.Add(selectionVars[(baseId, matchIndex)] == selectionVars[(otherId, i)]);
                        }
                        else
                        {
                            model.Add(selectionVars[(otherId, i)] == 0);
                        }
                    }
                }
            }

            foreach (var kvp in jointNotMap)
            {
                if (!subjectOptions.ContainsKey(kvp.Key))
                {
                    continue;
                }

                foreach (var other in kvp.Value)
                {
                    if (kvp.Key >= other || !subjectOptions.ContainsKey(other))
                    {
                        continue;
                    }

                    var optionsA = candidateSessionMap[kvp.Key];
                    var optionsB = candidateSessionMap[other];

                    for (var i = 0; i < optionsA.Count; i++)
                    {
                        for (var j = 0; j < optionsB.Count; j++)
                        {
                            if (optionsA[i].Session.SessionKey == optionsB[j].Session.SessionKey)
                            {
                                model.Add(selectionVars[(kvp.Key, i)] + selectionVars[(other, j)] <= 1);
                            }
                        }
                    }
                }
            }

            var objectiveTerms = new List<LinearExpr>();
            foreach (var subject in subjects)
            {
                if (!subjectOptions.TryGetValue(subject.ModelSubjectId, out var vars))
                {
                    continue;
                }

                var options = candidateSessionMap[subject.ModelSubjectId];
                for (var i = 0; i < vars.Count; i++)
                {
                    var weight = (long)Math.Max(1, subject.Priority) * 10_000L + Math.Max(1, subject.Duration);
                    weight *= options[i].Session.Index + 1;
                    objectiveTerms.Add(vars[i] * weight);
                }
            }

            if (objectiveTerms.Count > 0)
            {
                model.Minimize(LinearExpr.Sum(objectiveTerms));
            }

            var solver = new CpSolver
            {
                StringParameters = "max_time_in_seconds:3000,num_search_workers:0"
            };

            var status = solver.Solve(model);
            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                return null;
            }

            var selection = new Dictionary<int, int>();
            foreach (var subject in subjects)
            {
                if (!candidateSessionMap.TryGetValue(subject.ModelSubjectId, out var options) || options.Count == 0)
                {
                    continue;
                }

                var chosen = -1;
                for (var i = 0; i < options.Count; i++)
                {
                    if (solver.BooleanValue(selectionVars[(subject.ModelSubjectId, i)]))
                    {
                        chosen = i;
                        break;
                    }
                }

                if (chosen < 0)
                {
                    return null;
                }

                selection[subject.ModelSubjectId] = chosen;
            }

            return selection;
        }

        #endregion
        #region 场次时间排布

        private static Dictionary<int, SubjectSchedule>? BuildSessionTimetable(
            Dictionary<int, int> solverSelection,
            List<AIExamModelSubject> subjects,
            Dictionary<int, List<SessionOption>> candidateSessionMap,
            List<ExamSession> sessions,
            Dictionary<int, List<AIExamModelClass>> subjectClassMap,
            ClassRoomPlanner classRoomPlanner,
            Dictionary<int, int> subjectToGroup,
            Dictionary<int, string> forcedStartMap,
            Dictionary<int, HashSet<int>> whitelistRooms,
            Dictionary<int, HashSet<int>> blacklistRooms,
            Dictionary<int, AIExamModelRoom> roomLookup,
            int minGap,
            StringBuilder error,
            out SessionReassignmentRequest? retryRequest)
        {
            retryRequest = null;
            var schedule = new Dictionary<int, SubjectSchedule>();
            var sessionGroups = new Dictionary<string, List<SubjectSessionItem>>();
            foreach (var subject in subjects)
            {
                if (!solverSelection.TryGetValue(subject.ModelSubjectId, out var optionIndex))
                {
                    continue;
                }

                if (!candidateSessionMap.TryGetValue(subject.ModelSubjectId, out var options) || optionIndex < 0 || optionIndex >= options.Count)
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]未能获取有效的考试场次。");
                    return null;
                }

                if (!subjectClassMap.TryGetValue(subject.ModelSubjectId, out var classList))
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]缺少班级信息，无法排考。");
                    return null;
                }

                var session = options[optionIndex].Session;
                var forcedStart = forcedStartMap.TryGetValue(subject.ModelSubjectId, out var startText)
                    ? ParseDateTime(session.DateText, startText)
                    : null;

                if (forcedStart.HasValue && (forcedStart.Value < session.Start || forcedStart.Value.AddMinutes(subject.Duration) > session.End))
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]的固定开考时间[{startText}]不在场次[{session.DateText} {session.TimeNo}]范围内。");
                    return null;
                }

                var groupId = subjectToGroup.TryGetValue(subject.ModelSubjectId, out var gid) ? gid : -subject.ModelSubjectId;
                var item = new SubjectSessionItem(subject, session, classList, forcedStart, groupId);

                if (!sessionGroups.TryGetValue(session.SessionKey, out var list))
                {
                    list = new List<SubjectSessionItem>();
                    sessionGroups[session.SessionKey] = list;
                }
                list.Add(item);
            }

            var sessionLookup = sessions.ToDictionary(s => s.SessionKey);

            foreach (var kvp in sessionGroups)
            {
                if (!sessionLookup.TryGetValue(kvp.Key, out var session))
                {
                    continue;
                }

                var groups = kvp.Value
                    .GroupBy(item => item.GroupId)
                    .Select(g => new ScheduledGroup(g.Key, g.ToList()))
                    .OrderBy(g => g.HasForcedStart ? 0 : 1)
                    .ThenBy(g => g.ForcedStart ?? session.Start)
                    .ThenByDescending(g => g.MaxPriority)
                    .ThenByDescending(g => g.MaxDuration)
                    .ThenBy(g => g.Subjects.Count)
                    .ToList();

                var maxAttempts = Math.Max(6, groups.Count * 12);
                var attempt = 0;
                var sessionScheduled = false;
                string? lastFailure = null;
                SessionReassignmentRequest? sessionRetry = null;

                while (attempt < maxAttempts && !sessionScheduled)
                {
                    attempt++;
                    var roomUsage = new Dictionary<int, List<Interval>>();
                    var sessionAssignments = new Dictionary<int, SubjectSchedule>();
                    var needRetry = false;
                    string? attemptFailure = null;

                    foreach (var group in groups)
                    {
                        if (!TryScheduleGroup(
                            group,
                            session,
                            roomUsage,
                            classRoomPlanner,
                            roomLookup,
                            whitelistRooms,
                            blacklistRooms,
                            minGap,
                            out var start,
                            out var failure,
                            out var conflictingClasses,
                            out var conflictingAssignments,
                            out var requiresSessionChange))
                        {
                            string? subjectFailure = null;
                            string? plannerFailure = null;

                            if (conflictingClasses != null && conflictingClasses.Count > 0)
                            {
                                var subjectAdjusted = false;
                                foreach (var subjectItem in group.Subjects)
                                {
                                    if (classRoomPlanner.TryReassignSubjectRooms(
                                        subjectItem.Subject.ModelSubjectId,
                                        conflictingClasses,
                                        conflictingAssignments,
                                        roomUsage.Keys,
                                        out var overrideFailure))
                                    {
                                        subjectAdjusted = true;
                                    }
                                    else if (!string.IsNullOrWhiteSpace(overrideFailure))
                                    {
                                        subjectFailure = overrideFailure;
                                    }
                                }

                                if (subjectAdjusted)
                                {
                                    needRetry = true;
                                }
                                else if (classRoomPlanner.TryReassignRooms(conflictingClasses, conflictingAssignments, out plannerFailure))
                                {
                                    needRetry = true;
                                }
                            }

                            if (!needRetry)
                            {
                                if (!requiresSessionChange
                                    && conflictingClasses != null
                                    && conflictingClasses.Count > 0)
                                {
                                    requiresSessionChange = true;
                                }

                                if (requiresSessionChange)
                                {
                                    if (sessionRetry == null)
                                    {
                                        var subjectsNeedingChange = group.Subjects
                                            .Select(s => s.Subject.ModelSubjectId)
                                            .Distinct()
                                            .ToList();
                                        var retryReason = string.IsNullOrWhiteSpace(failure)
                                            ? $"场次[{session.TimeNo}]无法完成考场安排。"
                                            : failure;
                                        sessionRetry = new SessionReassignmentRequest(session, subjectsNeedingChange, retryReason);
                                    }

                                    attemptFailure = !string.IsNullOrWhiteSpace(subjectFailure)
                                        ? subjectFailure
                                        : !string.IsNullOrWhiteSpace(plannerFailure)
                                            ? plannerFailure
                                            : string.IsNullOrWhiteSpace(failure)
                                                ? $"场次[{session.TimeNo}]无法完成考场安排。"
                                                : failure;
                                }
                                else
                                {
                                    attemptFailure = !string.IsNullOrWhiteSpace(subjectFailure)
                                        ? subjectFailure
                                        : !string.IsNullOrWhiteSpace(plannerFailure)
                                            ? plannerFailure
                                            : string.IsNullOrWhiteSpace(failure)
                                                ? $"场次[{session.TimeNo}]无法完成考场安排。"
                                                : failure;
                                }
                            }

                            break;
                        }

                        foreach (var subjectItem in group.Subjects)
                        {
                            var end = start.AddMinutes(subjectItem.Subject.Duration);
                            sessionAssignments[subjectItem.Subject.ModelSubjectId] = new SubjectSchedule(session, start, end);

                            var recordedRooms = new HashSet<int>();
                            foreach (var cls in subjectItem.Classes)
                            {
                                var plan = classRoomPlanner.GetPlanForSubject(subjectItem.Subject.ModelSubjectId, cls.ModelClassId);
                                if (plan == null || plan.Count == 0)
                                {
                                    attemptFailure = $"班级[{cls.ModelClassName ?? cls.ModelClassId.ToString()}]未预分配考场，无法生成考试时间。";
                                    break;
                                }

                                foreach (var slice in plan)
                                {
                                    if (!recordedRooms.Add(slice.RoomId))
                                    {
                                        continue;
                                    }

                                    if (!roomUsage.TryGetValue(slice.RoomId, out var intervals))
                                    {
                                        intervals = new List<Interval>();
                                        roomUsage[slice.RoomId] = intervals;
                                    }

                                    intervals.Add(new Interval(start, end));
                                }
                            }

                            if (attemptFailure != null)
                            {
                                break;
                            }
                        }

                        if (needRetry || attemptFailure != null)
                        {
                            break;
                        }
                    }

                    if (needRetry)
                    {
                        continue;
                    }

                    if (attemptFailure != null)
                    {
                        if (!string.IsNullOrWhiteSpace(attemptFailure))
                        {
                            lastFailure = attemptFailure;
                        }
                        break;
                    }

                    sessionScheduled = true;
                    foreach (var assignment in sessionAssignments)
                    {
                        schedule[assignment.Key] = assignment.Value;
                    }
                }

                if (!sessionScheduled)
                {
                    if (sessionRetry != null)
                    {
                        retryRequest = sessionRetry;
                        return null;
                    }

                    var fallbackSubjectIds = groups
                        .SelectMany(g => g.Subjects)
                        .Select(s => s.Subject.ModelSubjectId)
                        .Distinct()
                        .Where(id =>
                            candidateSessionMap.TryGetValue(id, out var options)
                                && options.Any(opt => opt.Session.SessionKey != session.SessionKey))
                        .ToList();

                    if (fallbackSubjectIds.Count > 0)
                    {
                        retryRequest = new SessionReassignmentRequest(
                            session,
                            fallbackSubjectIds,
                            string.IsNullOrWhiteSpace(lastFailure)
                                ? $"场次[{session.TimeNo}]无法完成考场安排。"
                                : lastFailure);
                        return null;
                    }

                    error.AppendLine(string.IsNullOrWhiteSpace(lastFailure)
                        ? $"场次[{session.TimeNo}]无法完成考场安排。"
                        : lastFailure);
                    return null;
                }
            }

            return schedule;
        }

        private static bool TryScheduleGroup(
            ScheduledGroup group,
            ExamSession session,
            Dictionary<int, List<Interval>> roomUsage,
            ClassRoomPlanner classRoomPlanner,
            Dictionary<int, AIExamModelRoom> roomLookup,
            Dictionary<int, HashSet<int>> whitelistRooms,
            Dictionary<int, HashSet<int>> blacklistRooms,
            int minGap,
            out DateTime start,
            out string failure,
            out List<int>? conflictingClasses,
            out Dictionary<int, HashSet<int>>? conflictingAssignments,
            out bool requiresSessionChange)
        {
            start = DateTime.MinValue;
            failure = string.Empty;
            conflictingClasses = null;
            conflictingAssignments = null;
            requiresSessionChange = false;

            var requiredRooms = new List<RoomRequirement>();
            var roomClassMap = new Dictionary<int, HashSet<int>>();
            var roomAssignments = new Dictionary<int, List<(int ClassId, int Grade, int StudentCount, int SeatCapacity)>>();
            foreach (var subjectItem in group.Subjects)
            {
                foreach (var cls in subjectItem.Classes)
                {
                    var plan = classRoomPlanner.GetPlanForSubject(subjectItem.Subject.ModelSubjectId, cls.ModelClassId);
                    if (plan == null || plan.Count == 0)
                    {
                        failure = $"班级[{cls.ModelClassName ?? cls.ModelClassId.ToString()}]缺少考场分配。";
                        return false;
                    }

                    foreach (var slice in plan)
                    {
                        if (!roomLookup.TryGetValue(slice.RoomId, out var room))
                        {
                            failure = $"考场[{slice.RoomId}]不存在，无法安排考试。";
                            return false;
                        }

                        if (!string.IsNullOrWhiteSpace(subjectItem.Subject.ExamMode)
                            && !string.Equals(subjectItem.Subject.ExamMode, room.ExamMode, StringComparison.Ordinal))
                        {
                            failure = $"科目[{subjectItem.Subject.ModelSubjectName}]与考场[{room.ModelRoomName}]的考试形式不匹配。";
                            return false;
                        }

                        if (whitelistRooms.TryGetValue(subjectItem.Subject.ModelSubjectId, out var allowed)
                            && allowed.Count > 0 && !allowed.Contains(room.ModelRoomId))
                        {
                            failure = $"科目[{subjectItem.Subject.ModelSubjectName}]限制考场，无法使用[{room.ModelRoomName}]。";
                            return false;
                        }

                        if (blacklistRooms.TryGetValue(subjectItem.Subject.ModelSubjectId, out var denied)
                            && denied.Contains(room.ModelRoomId))
                        {
                            failure = $"科目[{subjectItem.Subject.ModelSubjectName}]禁止使用考场[{room.ModelRoomName}]。";
                            return false;
                        }

                        if (slice.StudentCount > room.SeatCount)
                        {
                            failure = $"考场[{room.ModelRoomName}]容量不足，无法容纳班级[{cls.ModelClassName ?? cls.ModelClassId.ToString()}]。";
                            return false;
                        }

                        if (!roomClassMap.TryGetValue(slice.RoomId, out var classSet))
                        {
                            classSet = new HashSet<int>();
                            roomClassMap[slice.RoomId] = classSet;
                        }
                        classSet.Add(cls.ModelClassId);

                        if (!roomAssignments.TryGetValue(slice.RoomId, out var allocations))
                        {
                            allocations = new List<(int ClassId, int Grade, int StudentCount, int SeatCapacity)>();
                            roomAssignments[slice.RoomId] = allocations;
                        }

                        allocations.Add((cls.ModelClassId, cls.NJ, slice.StudentCount, room.SeatCount));

                        requiredRooms.Add(new RoomRequirement(room.ModelRoomId, subjectItem.Subject.Duration));
                    }
                }
            }

            foreach (var kvp in roomClassMap)
            {
                if (!roomAssignments.TryGetValue(kvp.Key, out var allocations) || allocations.Count == 0)
                {
                    continue;
                }

                var roomName = roomLookup.TryGetValue(kvp.Key, out var conflictRoom)
                    ? conflictRoom.ModelRoomName ?? kvp.Key.ToString()
                    : kvp.Key.ToString();

                if (allocations.Count > 2)
                {
                    failure = $"考场[{roomName}]在同一时间安排了超过两个班级，无法满足规则。";
                    CaptureRoomConflict(kvp.Key, group, roomClassMap, out conflictingClasses, out conflictingAssignments);
                    return false;
                }

                if (allocations.Count > 1)
                {
                    var distinctGrades = allocations.Select(a => a.Grade).Distinct().Count();
                    if (distinctGrades < allocations.Count)
                    {
                        failure = $"考场[{roomName}]安排了相同年级的多个班级，违反考场分配规则。";
                        CaptureRoomConflict(kvp.Key, group, roomClassMap, out conflictingClasses, out conflictingAssignments);
                        return false;
                    }
                }

                var capacity = allocations[0].SeatCapacity;
                var totalStudents = allocations.Sum(a => a.StudentCount);
                if (totalStudents > capacity)
                {
                    failure = $"考场[{roomName}]座位不足，无法同时容纳分配的班级。";
                    CaptureRoomConflict(kvp.Key, group, roomClassMap, out conflictingClasses, out conflictingAssignments);
                    return false;
                }
            }

            var forcedSet = group.Subjects
                .Where(s => s.ForcedStart.HasValue)
                .Select(s => s.ForcedStart!.Value)
                .Distinct()
                .ToList();

            if (forcedSet.Count > 1)
            {
                failure = "同一联排科目存在多个不同的固定开考时间，无法同时安排。";
                return false;
            }

            var forcedStart = forcedSet.Count == 1 ? forcedSet[0] : (DateTime?)null;
            if (forcedStart.HasValue)
            {
                if (forcedStart.Value < session.Start || forcedStart.Value.Add(group.MaxDuration) > session.End)
                {
                    failure = $"固定开考时间[{forcedStart:HH:mm}]超出场次[{session.TimeNo}]范围。";
                    return false;
                }

                if (!RoomsAvailable(requiredRooms, forcedStart.Value, minGap, roomUsage, out var suggestion, out var conflictRoomId))
                {
                    failure = suggestion ?? "固定开考时间内考场冲突，无法安排考试。";
                    CaptureRoomConflict(conflictRoomId, group, roomClassMap, out conflictingClasses, out conflictingAssignments);
                    return false;
                }

                start = forcedStart.Value;
                return true;
            }

            var candidate = session.Start;
            var latest = session.End - group.MaxDuration;
            if (latest < session.Start)
            {
                failure = $"场次[{session.TimeNo}]时长不足以安排科目[{group.Subjects.First().Subject.ModelSubjectName}]。";
                return false;
            }

            int? lastConflictRoomId = null;

            while (candidate <= latest)
            {
                if (RoomsAvailable(requiredRooms, candidate, minGap, roomUsage, out var suggestion, out var conflictRoomId))
                {
                    start = candidate;
                    return true;
                }

                lastConflictRoomId = conflictRoomId;

                if (!DateTime.TryParse(suggestion, out var nextCandidate))
                {
                    nextCandidate = candidate.AddMinutes(1);
                }

                if (nextCandidate <= candidate)
                {
                    nextCandidate = candidate.AddMinutes(1);
                }

                candidate = nextCandidate;
            }

            failure = $"场次[{session.TimeNo}]无法满足科目[{group.Subjects.First().Subject.ModelSubjectName}]的时间安排，请调整配置。";
            requiresSessionChange = true;
            CaptureRoomConflict(lastConflictRoomId, group, roomClassMap, out conflictingClasses, out conflictingAssignments);
            return false;
        }

        private static void CaptureRoomConflict(
            int? roomId,
            ScheduledGroup group,
            Dictionary<int, HashSet<int>> roomClassMap,
            out List<int>? conflictingClasses,
            out Dictionary<int, HashSet<int>>? conflictingAssignments)
        {
            conflictingClasses = null;
            conflictingAssignments = null;

            if (roomId.HasValue && roomClassMap.TryGetValue(roomId.Value, out var classSet) && classSet.Count > 0)
            {
                conflictingClasses = classSet.ToList();
                conflictingAssignments = new Dictionary<int, HashSet<int>>
                {
                    [roomId.Value] = new HashSet<int>(classSet)
                };
                return;
            }

            var allClasses = group.Subjects
                .SelectMany(s => s.Classes)
                .Select(c => c.ModelClassId)
                .Distinct()
                .ToList();

            if (allClasses.Count > 0)
            {
                conflictingClasses = allClasses;
            }
        }

        private static bool RoomsAvailable(
            List<RoomRequirement> requirements,
            DateTime start,
            int minGap,
            Dictionary<int, List<Interval>> roomUsage,
            out string? suggestion,
            out int? conflictRoomId)
        {
            suggestion = null;
            conflictRoomId = null;
            foreach (var requirement in requirements)
            {
                var end = start.AddMinutes(requirement.DurationMinutes);
                if (roomUsage.TryGetValue(requirement.RoomId, out var intervals))
                {
                    foreach (var interval in intervals)
                    {
                        if (IntervalsOverlap(interval.Start, interval.End, start, end))
                        {
                            suggestion = interval.End.AddMinutes(minGap).ToString("yyyy-MM-dd HH:mm");
                            conflictRoomId = requirement.RoomId;
                            return false;
                        }

                        if (minGap > 0)
                        {
                            if ((start - interval.End).TotalMinutes >= 0 && (start - interval.End).TotalMinutes < minGap)
                            {
                                suggestion = interval.End.AddMinutes(minGap).ToString("yyyy-MM-dd HH:mm");
                                conflictRoomId = requirement.RoomId;
                                return false;
                            }

                            if ((interval.Start - end).TotalMinutes >= 0 && (interval.Start - end).TotalMinutes < minGap)
                            {
                                suggestion = interval.End.AddMinutes(minGap).ToString("yyyy-MM-dd HH:mm");
                                conflictRoomId = requirement.RoomId;
                                return false;
                            }
                        }
                    }
                }

            }

            return true;
        }

        #endregion
        #region 结果生成

        private static List<AIExamResult> BuildExamResults(
            List<AIExamModelSubject> subjects,
            Dictionary<int, List<AIExamModelClass>> subjectClassMap,
            ClassRoomPlanner classRoomPlanner,
            Dictionary<int, SubjectSchedule> subjectSchedules,
            Dictionary<int, AIExamModelRoom> roomLookup,
            Dictionary<int, HashSet<int>> whitelistRooms,
            Dictionary<int, HashSet<int>> blacklistRooms,
            StringBuilder error)
        {
            var results = new List<AIExamResult>();

            foreach (var subject in subjects)
            {
                if (!subjectSchedules.TryGetValue(subject.ModelSubjectId, out var schedule))
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]未能安排考试时间。");
                    continue;
                }

                if (!subjectClassMap.TryGetValue(subject.ModelSubjectId, out var classList) || classList.Count == 0)
                {
                    error.AppendLine($"科目[{subject.ModelSubjectName}]缺少班级信息，无法生成结果。");
                    continue;
                }

                foreach (var cls in classList)
                {
                    var plan = classRoomPlanner.GetPlanForSubject(subject.ModelSubjectId, cls.ModelClassId);
                    if (plan == null || plan.Count == 0)
                    {
                        error.AppendLine($"班级[{cls.ModelClassName ?? cls.ModelClassId.ToString()}]没有可用考场，无法生成考试安排。");
                        continue;
                    }

                    foreach (var slice in plan)
                    {
                        if (!roomLookup.TryGetValue(slice.RoomId, out var room))
                        {
                            error.AppendLine($"考场[{slice.RoomId}]不存在，无法生成考试安排。");
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(subject.ExamMode)
                            && !string.Equals(subject.ExamMode, room.ExamMode, StringComparison.Ordinal))
                        {
                            error.AppendLine($"科目[{subject.ModelSubjectName}]与考场[{room.ModelRoomName}]考试形式不一致。");
                            continue;
                        }

                        if (whitelistRooms.TryGetValue(subject.ModelSubjectId, out var allowed)
                            && allowed.Count > 0 && !allowed.Contains(room.ModelRoomId))
                        {
                            error.AppendLine($"科目[{subject.ModelSubjectName}]限制考场，不能使用[{room.ModelRoomName}]。");
                            continue;
                        }

                        if (blacklistRooms.TryGetValue(subject.ModelSubjectId, out var denied)
                            && denied.Contains(room.ModelRoomId))
                        {
                            error.AppendLine($"科目[{subject.ModelSubjectName}]禁止使用考场[{room.ModelRoomName}]。");
                            continue;
                        }

                        results.Add(new AIExamResult
                        {
                            ModelSubjectId = subject.ModelSubjectId,
                            ModelRoomId = room.ModelRoomId,
                            ModelClassId = cls.ModelClassId,
                            Duration = subject.Duration,
                            Date = schedule.DateText,
                            StartTime = schedule.Start.ToString("HH:mm"),
                            EndTime = schedule.End.ToString("HH:mm"),
                            StudentCount = slice.StudentCount,
                            SeatCount = room.SeatCount,
                            TeacherList = new List<AIExamTeacherResult>()
                        });
                    }
                }
            }

            return results;
        }

        #endregion
        #region 监考教师分配

        private static void AssignInvigilators(
            List<AIExamResult> scheduled,
            AIExamModel model,
            Dictionary<int, AIExamModelRoom> roomLookup)
        {
            if (scheduled == null || scheduled.Count == 0)
            {
                return;
            }

            var teachers = model.ModelTeacherList;
            if (teachers == null || teachers.Count == 0)
            {
                return;
            }

            var teacherStates = teachers
                .Select(t => new TeacherState(t))
                .ToDictionary(t => t.Teacher.ModelTeacherId, t => t);

            var allowBuildings = BuildTeacherRuleLookup(model.RuleTeacherBuildingList);
            var denyBuildings = BuildTeacherRuleLookup(model.RuleTeacherBuildingNotList);
            var allowClasses = BuildTeacherRuleLookup(model.RuleTeacherClassList);
            var denyClasses = BuildTeacherRuleLookup(model.RuleTeacherClassNotList);
            var allowSubjects = BuildTeacherRuleLookup(model.RuleTeacherSubjectList);
            var denySubjects = BuildTeacherRuleLookup(model.RuleTeacherSubjectNotList);
            var unavailable = BuildTeacherUnavailability(model.RuleTeacherUnTimeList);

            var grouped = scheduled
                .GroupBy(x => (x.Date ?? string.Empty, x.ModelRoomId))
                .OrderBy(g => g.Key.Item1)
                .ThenBy(g => g.Key.ModelRoomId);

            foreach (var group in grouped)
            {
                if (!roomLookup.TryGetValue(group.Key.ModelRoomId, out var room))
                {
                    continue;
                }

                var requiredTeachers = Math.Max(0, room.TeacherCount);
                if (requiredTeachers == 0)
                {
                    foreach (var exam in group)
                    {
                        exam.TeacherList = new List<AIExamTeacherResult>();
                    }
                    continue;
                }

                var classIds = group.Select(x => x.ModelClassId).Distinct().ToHashSet();
                var subjectIds = group.Select(x => x.ModelSubjectId).Distinct().ToHashSet();

                var intervals = new List<Interval>();
                var validTimes = true;
                foreach (var exam in group)
                {
                    var start = ParseDateTime(group.Key.Item1, exam.StartTime);
                    var end = ParseDateTime(group.Key.Item1, exam.EndTime);
                    if (!start.HasValue || !end.HasValue)
                    {
                        validTimes = false;
                        break;
                    }
                    intervals.Add(new Interval(start.Value, end.Value));
                }

                if (!validTimes)
                {
                    continue;
                }

                var candidates = new List<TeacherState>();
                foreach (var teacher in teacherStates.Values)
                {
                    if (IsTeacherEligible(
                        teacher,
                        group.Key.Item1,
                        room,
                        classIds,
                        subjectIds,
                        intervals,
                        allowBuildings,
                        denyBuildings,
                        allowClasses,
                        denyClasses,
                        allowSubjects,
                        denySubjects,
                        unavailable))
                    {
                        candidates.Add(teacher);
                    }
                }

                if (candidates.Count == 0)
                {
                    foreach (var exam in group)
                    {
                        exam.TeacherList = new List<AIExamTeacherResult>();
                    }
                    continue;
                }

                var selected = SelectTeachers(candidates, requiredTeachers);
                foreach (var teacher in selected)
                {
                    teacher.AssignmentCount++;
                    teacher.DailyRooms[group.Key.Item1] = room.ModelRoomId;
                }

                var teacherResults = selected
                    .Select(t => new AIExamTeacherResult { ModelTeacherId = t.Teacher.ModelTeacherId })
                    .ToList();

                foreach (var exam in group)
                {
                    exam.TeacherList = teacherResults
                        .Select(t => new AIExamTeacherResult { ModelTeacherId = t.ModelTeacherId })
                        .ToList();
                }
            }
        }

        private static bool IsTeacherEligible(
            TeacherState teacher,
            string date,
            AIExamModelRoom room,
            HashSet<int> classIds,
            HashSet<int> subjectIds,
            List<Interval> intervals,
            Dictionary<int, HashSet<int>> allowBuildings,
            Dictionary<int, HashSet<int>> denyBuildings,
            Dictionary<int, HashSet<int>> allowClasses,
            Dictionary<int, HashSet<int>> denyClasses,
            Dictionary<int, HashSet<int>> allowSubjects,
            Dictionary<int, HashSet<int>> denySubjects,
            Dictionary<int, List<Interval>> unavailable)
        {
            var teacherId = teacher.Teacher.ModelTeacherId;

            if (teacher.DailyRooms.TryGetValue(date, out var assignedRoom) && assignedRoom != room.ModelRoomId)
            {
                return false;
            }

            if (allowBuildings.TryGetValue(teacherId, out var allowedBuildings) && allowedBuildings.Count > 0 && !allowedBuildings.Contains(room.BuildingId))
            {
                return false;
            }

            if (denyBuildings.TryGetValue(teacherId, out var deniedBuildings) && deniedBuildings.Contains(room.BuildingId))
            {
                return false;
            }

            if (allowClasses.TryGetValue(teacherId, out var allowedClasses) && allowedClasses.Count > 0 && classIds.Any() && !classIds.All(allowedClasses.Contains))
            {
                return false;
            }

            if (denyClasses.TryGetValue(teacherId, out var deniedClasses) && classIds.Any(deniedClasses.Contains))
            {
                return false;
            }

            if (allowSubjects.TryGetValue(teacherId, out var allowedSubjects) && allowedSubjects.Count > 0 && subjectIds.Any() && !subjectIds.All(allowedSubjects.Contains))
            {
                return false;
            }

            if (denySubjects.TryGetValue(teacherId, out var deniedSubjects) && subjectIds.Any(deniedSubjects.Contains))
            {
                return false;
            }

            if (unavailable.TryGetValue(teacherId, out var blocks))
            {
                foreach (var examInterval in intervals)
                {
                    foreach (var block in blocks)
                    {
                        if (IntervalsOverlap(examInterval.Start, examInterval.End, block.Start, block.End))
                        {
                            return false;
                        }
                    }
                }
            }

            if (teacher.DailyRooms.TryGetValue(date, out var existingRoom) && existingRoom != room.ModelRoomId)
            {
                return false;
            }

            return true;
        }

        private static List<TeacherState> SelectTeachers(List<TeacherState> candidates, int required)
        {
            var selection = new List<TeacherState>();
            if (required <= 0 || candidates.Count == 0)
            {
                return selection;
            }

            var used = new HashSet<int>();
            var maleCandidates = OrderTeachers(candidates.Where(t => t.Teacher.Gender == 1)).ToList();
            var femaleCandidates = OrderTeachers(candidates.Where(t => t.Teacher.Gender != 1)).ToList();

            var maleTarget = required / 2;
            var femaleTarget = required / 2;
            if (required % 2 == 1)
            {
                if (femaleCandidates.Count > maleCandidates.Count)
                {
                    femaleTarget++;
                }
                else
                {
                    maleTarget++;
                }
            }

            AddCandidates(maleCandidates, ref maleTarget, selection, used);
            AddCandidates(femaleCandidates, ref femaleTarget, selection, used);

            if (selection.Count < required)
            {
                var remaining = required - selection.Count;
                var rest = OrderTeachers(candidates.Where(t => !used.Contains(t.Teacher.ModelTeacherId))).ToList();
                AddCandidates(rest, ref remaining, selection, used);
            }

            return selection;
        }

        private static IOrderedEnumerable<TeacherState> OrderTeachers(IEnumerable<TeacherState> teachers)
        {
            return teachers
                .OrderBy(t => t.AssignmentCount)
                .ThenBy(t => t.DailyRooms.Count)
                .ThenBy(t => t.Teacher.ModelTeacherId);
        }

        private static void AddCandidates(
            IEnumerable<TeacherState> ordered,
            ref int needed,
            List<TeacherState> selection,
            HashSet<int> used)
        {
            if (needed <= 0)
            {
                return;
            }

            foreach (var teacher in ordered)
            {
                if (needed <= 0)
                {
                    break;
                }

                if (!used.Add(teacher.Teacher.ModelTeacherId))
                {
                    continue;
                }

                selection.Add(teacher);
                needed--;
            }
        }

        #endregion
        #region 通用工具

        private static Dictionary<int, string> BuildForcedDateMap(AIExamModel model)
        {
            var map = new Dictionary<int, string>();

            if (model.ModelSubjectList != null)
            {
                foreach (var subject in model.ModelSubjectList)
                {
                    if (!string.IsNullOrWhiteSpace(subject?.Date))
                    {
                        map[subject!.ModelSubjectId] = subject.Date!;
                    }
                }
            }

            if (model.RuleJointSubjectList != null)
            {
                foreach (var rule in model.RuleJointSubjectList)
                {
                    if (string.IsNullOrWhiteSpace(rule?.Date) || rule.RuleJointSubjectList == null)
                    {
                        continue;
                    }

                    foreach (var item in rule.RuleJointSubjectList)
                    {
                        map[item.ModelSubjectId] = rule.Date!;
                    }
                }
            }

            return map;
        }

        private static Dictionary<int, string> BuildForcedStartMap(AIExamModel model)
        {
            var map = new Dictionary<int, string>();

            if (model.ModelSubjectList != null)
            {
                foreach (var subject in model.ModelSubjectList)
                {
                    if (!string.IsNullOrWhiteSpace(subject?.StartTime))
                    {
                        map[subject!.ModelSubjectId] = subject.StartTime!;
                    }
                }
            }

            if (model.RuleJointSubjectList != null)
            {
                foreach (var rule in model.RuleJointSubjectList)
                {
                    if (string.IsNullOrWhiteSpace(rule?.StartTime) || rule.RuleJointSubjectList == null)
                    {
                        continue;
                    }

                    foreach (var item in rule.RuleJointSubjectList)
                    {
                        map[item.ModelSubjectId] = rule.StartTime!;
                    }
                }
            }

            return map;
        }

        private static List<HashSet<int>> BuildJointSubjectGroups(List<AIExamRuleJointSubject>? rules)
        {
            var result = new List<HashSet<int>>();
            if (rules == null)
            {
                return result;
            }

            foreach (var rule in rules)
            {
                if (rule?.RuleJointSubjectList == null || rule.RuleJointSubjectList.Count == 0)
                {
                    continue;
                }

                var ids = rule.RuleJointSubjectList
                    .Select(x => x.ModelSubjectId)
                    .Distinct()
                    .ToList();

                if (ids.Count > 1)
                {
                    result.Add(ids.ToHashSet());
                }
            }

            return result;
        }

        private static Dictionary<int, int> BuildSubjectToGroupMap(List<HashSet<int>> groups)
        {
            var map = new Dictionary<int, int>();
            for (var i = 0; i < groups.Count; i++)
            {
                foreach (var subjectId in groups[i])
                {
                    map[subjectId] = i + 1;
                }
            }

            return map;
        }

        private static Dictionary<int, HashSet<int>> BuildJointSubjectNotMap(List<AIExamRuleJointSubjectNot>? rules)
        {
            var map = new Dictionary<int, HashSet<int>>();
            if (rules == null)
            {
                return map;
            }

            foreach (var rule in rules)
            {
                if (rule?.RuleJointSubjectList == null || rule.RuleJointSubjectList.Count < 2)
                {
                    continue;
                }

                var ids = rule.RuleJointSubjectList
                    .Select(x => x.ModelSubjectId)
                    .Distinct()
                    .ToList();

                foreach (var id in ids)
                {
                    if (!map.TryGetValue(id, out var set))
                    {
                        set = new HashSet<int>();
                        map[id] = set;
                    }

                    foreach (var other in ids)
                    {
                        if (other != id)
                        {
                            set.Add(other);
                        }
                    }
                }
            }

            return map;
        }

        private static Dictionary<int, HashSet<int>> BuildTeacherRuleLookup<T>(List<T>? rules)
            where T : class, ITeacherRule
        {
            var map = new Dictionary<int, HashSet<int>>();
            if (rules == null)
            {
                return map;
            }

            foreach (var rule in rules)
            {
                if (rule.TeacherId <= 0 || rule.TargetId <= 0)
                {
                    continue;
                }

                if (!map.TryGetValue(rule.TeacherId, out var set))
                {
                    set = new HashSet<int>();
                    map[rule.TeacherId] = set;
                }

                set.Add(rule.TargetId);
            }

            return map;
        }

        private static Dictionary<int, HashSet<int>> BuildRoomRuleLookup(List<AIExamRuleRoomSubject>? rules)
        {
            var map = new Dictionary<int, HashSet<int>>();
            if (rules == null)
            {
                return map;
            }

            foreach (var rule in rules)
            {
                if (rule.ModelSubjectId <= 0 || rule.ModelRoomId <= 0)
                {
                    continue;
                }

                if (!map.TryGetValue(rule.ModelSubjectId, out var set))
                {
                    set = new HashSet<int>();
                    map[rule.ModelSubjectId] = set;
                }

                set.Add(rule.ModelRoomId);
            }

            return map;
        }

        private static Dictionary<int, List<Interval>> BuildTeacherUnavailability(List<AIExamRuleTeacherUnTime>? rules)
        {
            var map = new Dictionary<int, List<Interval>>();
            if (rules == null)
            {
                return map;
            }

            foreach (var rule in rules)
            {
                if (rule?.ModelTeacherId <= 0 || string.IsNullOrWhiteSpace(rule.Date) || string.IsNullOrWhiteSpace(rule.StartTime) || string.IsNullOrWhiteSpace(rule.EndTime))
                {
                    continue;
                }

                var start = ParseDateTime(rule.Date, rule.StartTime);
                var end = ParseDateTime(rule.Date, rule.EndTime);
                if (!start.HasValue || !end.HasValue || start.Value >= end.Value)
                {
                    continue;
                }

                if (!map.TryGetValue(rule.ModelTeacherId, out var list))
                {
                    list = new List<Interval>();
                    map[rule.ModelTeacherId] = list;
                }

                list.Add(new Interval(start.Value, end.Value));
            }

            return map;
        }

        private static DateTime? ParseDateTime(string? date, string? time)
        {
            if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time))
            {
                return null;
            }

            if (DateTime.TryParseExact($"{date.Trim()} {time.Trim()}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                return value;
            }

            return null;
        }

        private static bool IntervalsOverlap(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
        {
            return aStart < bEnd && bStart < aEnd;
        }

        #endregion
        #region 辅助实体

        private sealed class ExamSession
        {
            public ExamSession(string date, string timeNo, DateTime start, DateTime end, int index)
            {
                DateText = date;
                TimeNo = timeNo;
                Start = start;
                End = end;
                Index = index;
                SessionKey = $"{date}_{timeNo}";
            }

            public string DateText { get; }
            public string TimeNo { get; }
            public DateTime Start { get; }
            public DateTime End { get; }
            public int Index { get; }
            public string SessionKey { get; }
        }

        private sealed class SessionOption
        {
            public SessionOption(ExamSession session)
            {
                Session = session;
            }

            public ExamSession Session { get; }
        }

        private sealed class SessionReassignmentRequest
        {
            public SessionReassignmentRequest(ExamSession session, List<int> subjectIds, string? reason)
            {
                Session = session;
                SubjectIds = subjectIds ?? new List<int>();
                Reason = reason;
            }

            public ExamSession Session { get; }
            public List<int> SubjectIds { get; }
            public string? Reason { get; }
        }

        private sealed class ClassRoomPlanner
        {
            private readonly List<AIExamModelClass> _classes;
            private readonly List<AIExamModelRoom> _rooms;
            private readonly Dictionary<int, AIExamModelClass> _classLookup;
            private readonly Dictionary<int, AIExamModelRoom> _roomLookup;
            private readonly Dictionary<int, List<ClassRoomSlice>> _plans = new Dictionary<int, List<ClassRoomSlice>>();
            private readonly Dictionary<int, HashSet<int>> _roomGradeUsage = new Dictionary<int, HashSet<int>>();
            private readonly Dictionary<int, int> _roomSeatUsage = new Dictionary<int, int>();
            private readonly Dictionary<int, Dictionary<int, List<ClassRoomSlice>>> _subjectOverrides
                = new Dictionary<int, Dictionary<int, List<ClassRoomSlice>>>();
            private readonly Dictionary<int, Dictionary<int, HashSet<int>>> _subjectRoomBans
                = new Dictionary<int, Dictionary<int, HashSet<int>>>();

            private ClassRoomPlanner(List<AIExamModelClass> classes, List<AIExamModelRoom> rooms)
            {
                _classes = classes ?? new List<AIExamModelClass>();
                _rooms = rooms ?? new List<AIExamModelRoom>();
                _classLookup = _classes.ToDictionary(c => c.ModelClassId);
                _roomLookup = _rooms.ToDictionary(r => r.ModelRoomId);
            }

            public Dictionary<int, List<ClassRoomSlice>> Plans => _plans;

            public List<ClassRoomSlice>? GetPlanForSubject(int subjectId, int classId)
            {
                if (subjectId > 0
                    && _subjectOverrides.TryGetValue(subjectId, out var subjectMap)
                    && subjectMap.TryGetValue(classId, out var overridePlan))
                {
                    return overridePlan;
                }

                return _plans.TryGetValue(classId, out var plan) ? plan : null;
            }

            public static ClassRoomPlanner Build(List<AIExamModelClass> classes, List<AIExamModelRoom> rooms, StringBuilder error)
            {
                var planner = new ClassRoomPlanner(classes, rooms);
                planner.Initialize(error);
                return planner;
            }

            private bool Initialize(StringBuilder error)
            {
                _plans.Clear();
                _roomGradeUsage.Clear();
                _roomSeatUsage.Clear();
                _subjectOverrides.Clear();
                _subjectRoomBans.Clear();

                if (_classes.Count == 0 || _rooms.Count == 0)
                {
                    return true;
                }

                foreach (var gradeGroup in _classes.GroupBy(c => c.NJ).OrderBy(g => g.Key))
                {
                    if (!TryAssignGrade(gradeGroup.Key, gradeGroup.ToList(), null, error))
                    {
                        return false;
                    }
                }

                return true;
            }

            public bool TryReassignRooms(IEnumerable<int> classIds, Dictionary<int, HashSet<int>>? conflictingRooms, out string? failure)
            {
                failure = null;
                if (classIds == null && (conflictingRooms == null || conflictingRooms.Count == 0))
                {
                    failure = "未提供需要调整的班级。";
                    return false;
                }

                var gradeSet = new HashSet<int>();

                if (classIds != null)
                {
                    foreach (var id in classIds)
                    {
                        if (_classLookup.TryGetValue(id, out var cls))
                        {
                            gradeSet.Add(cls.NJ);
                        }
                    }
                }

                if (conflictingRooms != null)
                {
                    foreach (var kvp in conflictingRooms)
                    {
                        if (_roomGradeUsage.TryGetValue(kvp.Key, out var grades))
                        {
                            foreach (var grade in grades)
                            {
                                gradeSet.Add(grade);
                            }
                        }

                        foreach (var classId in kvp.Value)
                        {
                            if (_classLookup.TryGetValue(classId, out var cls))
                            {
                                gradeSet.Add(cls.NJ);
                            }
                        }
                    }
                }

                var targetGrades = gradeSet
                    .OrderBy(g => g)
                    .ToList();

                if (targetGrades.Count == 0)
                {
                    failure = "未能识别需要调整的班级所属年级。";
                    return false;
                }

                var gradeAvoidRooms = BuildGradeAvoidRooms(gradeSet, conflictingRooms);

                var backupPlans = ClonePlans();
                var backupUsage = CloneUsage();
                var backupOverrides = CloneOverrides();
                var backupBans = CloneSubjectBans();
                var backupSeatUsage = CloneSeatUsage();

                var anyChange = false;
                string? lastFailure = null;

                foreach (var grade in targetGrades)
                {
                    var gradeClasses = _classes.Where(c => c.NJ == grade).ToList();
                    if (gradeClasses.Count == 0)
                    {
                        continue;
                    }

                    var originalPlans = SnapshotGradePlans(grade, backupPlans);
                    var originalOverrides = SnapshotGradeOverrides(grade, backupOverrides);
                    var originalBans = SnapshotGradeBans(grade, backupBans);

                    var avoid = gradeAvoidRooms.TryGetValue(grade, out var set) && set.Count > 0
                        ? new HashSet<int>(set)
                        : null;

                    RemoveGradeAssignments(grade);

                    if (!TryBuildGradePlans(grade, gradeClasses, avoid, out var gradePlans, out var gradeFailure))
                    {
                        lastFailure = gradeFailure ?? lastFailure;
                        RestoreGradeAssignments(grade, originalPlans);
                        RestoreGradeOverrides(grade, originalOverrides);
                        RestoreGradeBans(grade, originalBans);
                        continue;
                    }

                    if (!CommitGradePlans(grade, gradePlans, out gradeFailure))
                    {
                        lastFailure = gradeFailure ?? lastFailure;
                        RestoreGradeAssignments(grade, originalPlans);
                        RestoreGradeOverrides(grade, originalOverrides);
                        RestoreGradeBans(grade, originalBans);
                        continue;
                    }

                    if (!anyChange && gradeClasses.Any(cls =>
                        !ArePlansEqual(
                            _plans.TryGetValue(cls.ModelClassId, out var newPlan) ? newPlan : new List<ClassRoomSlice>(),
                            originalPlans.TryGetValue(cls.ModelClassId, out var oldPlan) ? oldPlan : new List<ClassRoomSlice>())))
                    {
                        anyChange = true;
                    }
                }

                if (!anyChange)
                {
                    RestorePlans(backupPlans);
                    RestoreUsage(backupUsage);
                    RestoreSeatUsage(backupSeatUsage);
                    RestoreOverrides(backupOverrides);
                    RestoreSubjectBans(backupBans);
                    failure = lastFailure ?? "冲突班级未能找到新的考场组合。";
                    return false;
                }

                return true;
            }

            public bool TryReassignSubjectRooms(
                int subjectId,
                IEnumerable<int> classIds,
                Dictionary<int, HashSet<int>>? conflictingRooms,
                IEnumerable<int>? occupiedRooms,
                out string? failure)
            {
                failure = null;

                if (subjectId <= 0)
                {
                    failure = "科目信息无效，无法重新分配考场。";
                    return false;
                }

                var classSet = classIds?
                    .Where(id => _classLookup.ContainsKey(id))
                    .Select(id => _classLookup[id])
                    .Distinct()
                    .OrderByDescending(c => c.StudentCount)
                    .ThenBy(c => c.ModelClassId)
                    .ToList() ?? new List<AIExamModelClass>();

                if (classSet.Count == 0)
                {
                    failure = "未能识别需要重新分配考场的班级。";
                    return false;
                }

                var targetClassIds = new HashSet<int>(classSet.Select(c => c.ModelClassId));

                if (conflictingRooms != null)
                {
                    foreach (var kvp in conflictingRooms)
                    {
                        if (kvp.Key <= 0)
                        {
                            continue;
                        }

                        foreach (var classId in kvp.Value)
                        {
                            if (!targetClassIds.Contains(classId))
                            {
                                continue;
                            }

                            var banSet = GetOrCreateSubjectBanSet(subjectId, classId);
                            banSet.Add(kvp.Key);
                        }
                    }
                }

                var roomSnapshots = _rooms
                    .Select(r => new RoomSnapshot(r, GetAvailableSeatCount(r.ModelRoomId)))
                    .ToList();

                if (roomSnapshots.Count == 0)
                {
                    failure = "没有可用考场。";
                    return false;
                }

                var buildings = roomSnapshots
                    .GroupBy(r => r.BuildingId)
                    .Select(g => new BuildingRooms(
                        g.Key,
                        g.Select(x => new RoomSnapshot(
                                x.RoomId,
                                x.BuildingId,
                                x.RoomNo,
                                _roomLookup.TryGetValue(x.RoomId, out var info) ? info.SeatCount : x.Capacity,
                                x.AvailableSeats))
                            .OrderBy(x => x.RoomNo ?? int.MaxValue)
                            .ThenBy(x => x.AvailableSeats)
                            .ToList(),
                        g.Sum(x => x.AvailableSeats)))
                    .OrderByDescending(b => b.TotalSeats)
                    .ThenBy(b => b.BuildingId)
                    .ToList();

                var usedRooms = new HashSet<int>();
                var overrides = new Dictionary<int, List<ClassRoomSlice>>();
                string? lastFailure = null;
                var seededRooms = new Dictionary<int, List<int>>();

                void RollbackSeededRooms()
                {
                    if (seededRooms.Count == 0)
                    {
                        return;
                    }

                    if (!_subjectRoomBans.TryGetValue(subjectId, out var subjectBanMap))
                    {
                        seededRooms.Clear();
                        return;
                    }

                    foreach (var kvp in seededRooms)
                    {
                        if (!subjectBanMap.TryGetValue(kvp.Key, out var banSet))
                        {
                            continue;
                        }

                        foreach (var roomId in kvp.Value)
                        {
                            banSet.Remove(roomId);
                        }

                        if (banSet.Count == 0)
                        {
                            subjectBanMap.Remove(kvp.Key);
                        }
                    }

                    if (subjectBanMap.Count == 0)
                    {
                        _subjectRoomBans.Remove(subjectId);
                    }

                    seededRooms.Clear();
                }

                var occupiedSet = occupiedRooms?.Where(id => id > 0).ToHashSet() ?? new HashSet<int>();

                foreach (var cls in classSet)
                {
                    var banSet = GetOrCreateSubjectBanSet(subjectId, cls.ModelClassId);
                    List<int>? seeded = null;

                    var currentPlan = GetPlanForSubject(subjectId, cls.ModelClassId);
                    if (currentPlan != null)
                    {
                        seeded = new List<int>();
                        foreach (var slice in currentPlan)
                        {
                            if (slice.RoomId <= 0)
                            {
                                continue;
                            }

                            if (banSet.Add(slice.RoomId))
                            {
                                seeded.Add(slice.RoomId);
                            }
                        }

                        if (seeded.Count > 0)
                        {
                            seededRooms[cls.ModelClassId] = seeded;
                        }
                    }

                    HashSet<int>? strictAvoid = null;
                    if (banSet.Count > 0)
                    {
                        strictAvoid = new HashSet<int>(banSet);
                    }

                    HashSet<int>? softAvoid = null;
                    if (occupiedSet.Count > 0)
                    {
                        softAvoid = strictAvoid != null
                            ? new HashSet<int>(strictAvoid)
                            : new HashSet<int>();

                        foreach (var roomId in occupiedSet)
                        {
                            softAvoid.Add(roomId);
                        }
                    }

                    List<ClassRoomSlice>? allocation = null;

                    if (softAvoid != null)
                    {
                        allocation = AllocateRoomsForClass(cls, buildings, usedRooms, softAvoid);
                    }

                    if (allocation == null || allocation.Count == 0)
                    {
                        allocation = AllocateRoomsForClass(cls, buildings, usedRooms, strictAvoid);
                    }
                    if (allocation == null || allocation.Count == 0)
                    {
                        lastFailure = $"班级[{cls.ModelClassName ?? cls.ModelClassId.ToString()}]无法找到新的考场组合。";
                        RollbackSeededRooms();
                        failure = lastFailure;
                        return false;
                    }

                    overrides[cls.ModelClassId] = allocation;
                    foreach (var slice in allocation)
                    {
                        usedRooms.Add(slice.RoomId);
                    }
                }

                var anyChange = false;
                foreach (var kvp in overrides)
                {
                    var currentPlan = GetPlanForSubject(subjectId, kvp.Key) ?? new List<ClassRoomSlice>();
                    if (!ArePlansEqual(kvp.Value, currentPlan))
                    {
                        anyChange = true;
                        break;
                    }
                }

                if (!anyChange)
                {
                    RollbackSeededRooms();
                    failure = lastFailure ?? "未能找到新的考场组合。";
                    return false;
                }

                if (!_subjectOverrides.TryGetValue(subjectId, out var subjectMap))
                {
                    subjectMap = new Dictionary<int, List<ClassRoomSlice>>();
                    _subjectOverrides[subjectId] = subjectMap;
                }

                foreach (var kvp in overrides)
                {
                    subjectMap[kvp.Key] = kvp.Value.Select(slice => slice.Clone()).ToList();
                }

                seededRooms.Clear();

                return true;
            }

            private void ClearSubjectOverridesForClasses(IEnumerable<int> classIds)
            {
                if (_subjectOverrides.Count == 0)
                {
                    return;
                }

                var targets = new HashSet<int>(classIds ?? Array.Empty<int>());
                if (targets.Count == 0)
                {
                    return;
                }

                var emptySubjects = new List<int>();
                foreach (var kvp in _subjectOverrides)
                {
                    var overrides = kvp.Value;
                    var keysToRemove = overrides.Keys.Where(targets.Contains).ToList();
                    foreach (var key in keysToRemove)
                    {
                        overrides.Remove(key);
                    }

                    if (overrides.Count == 0)
                    {
                        emptySubjects.Add(kvp.Key);
                    }
                }

                foreach (var subjectId in emptySubjects)
                {
                    _subjectOverrides.Remove(subjectId);
                }
            }

            private HashSet<int> GetOrCreateSubjectBanSet(int subjectId, int classId)
            {
                if (!_subjectRoomBans.TryGetValue(subjectId, out var subjectMap))
                {
                    subjectMap = new Dictionary<int, HashSet<int>>();
                    _subjectRoomBans[subjectId] = subjectMap;
                }

                if (!subjectMap.TryGetValue(classId, out var banSet))
                {
                    banSet = new HashSet<int>();
                    subjectMap[classId] = banSet;
                }

                return banSet;
            }

            private HashSet<int>? TryGetSubjectBanSet(int subjectId, int classId)
            {
                return _subjectRoomBans.TryGetValue(subjectId, out var subjectMap)
                    && subjectMap.TryGetValue(classId, out var banSet)
                    ? banSet
                    : null;
            }

            private void ClearSubjectBansForClasses(IEnumerable<int> classIds)
            {
                if (_subjectRoomBans.Count == 0)
                {
                    return;
                }

                var targets = new HashSet<int>(classIds ?? Array.Empty<int>());
                if (targets.Count == 0)
                {
                    return;
                }

                var emptySubjects = new List<int>();
                foreach (var kvp in _subjectRoomBans)
                {
                    var bans = kvp.Value;
                    var keysToRemove = bans.Keys.Where(targets.Contains).ToList();
                    foreach (var key in keysToRemove)
                    {
                        bans.Remove(key);
                    }

                    if (bans.Count == 0)
                    {
                        emptySubjects.Add(kvp.Key);
                    }
                }

                foreach (var subjectId in emptySubjects)
                {
                    _subjectRoomBans.Remove(subjectId);
                }
            }

            private void ClearSubjectBans(int subjectId, IEnumerable<int> classIds)
            {
                if (!_subjectRoomBans.TryGetValue(subjectId, out var subjectMap))
                {
                    return;
                }

                var targets = classIds?.ToList() ?? new List<int>();
                foreach (var classId in targets)
                {
                    subjectMap.Remove(classId);
                }

                if (subjectMap.Count == 0)
                {
                    _subjectRoomBans.Remove(subjectId);
                }
            }

            private Dictionary<int, HashSet<int>> BuildGradeAvoidRooms(HashSet<int> targetGrades, Dictionary<int, HashSet<int>>? conflictingRooms)
            {
                var map = new Dictionary<int, HashSet<int>>();
                if (targetGrades == null || targetGrades.Count == 0 || conflictingRooms == null)
                {
                    return map;
                }

                foreach (var kvp in conflictingRooms)
                {
                    var roomId = kvp.Key;

                    void TagGrade(int grade)
                    {
                        if (!targetGrades.Contains(grade))
                        {
                            return;
                        }

                        if (!map.TryGetValue(grade, out var set))
                        {
                            set = new HashSet<int>();
                            map[grade] = set;
                        }

                        set.Add(roomId);
                    }

                    if (_roomGradeUsage.TryGetValue(roomId, out var grades))
                    {
                        foreach (var grade in grades)
                        {
                            TagGrade(grade);
                        }
                    }

                    foreach (var classId in kvp.Value)
                    {
                        if (_classLookup.TryGetValue(classId, out var cls))
                        {
                            TagGrade(cls.NJ);
                        }
                    }
                }

                return map;
            }

            private bool TryAssignGrade(int grade, List<AIExamModelClass> gradeClasses, HashSet<int>? avoidRooms, StringBuilder error)
            {
                if (!TryBuildGradePlans(grade, gradeClasses, avoidRooms, out var gradePlans, out var failure))
                {
                    if (!string.IsNullOrEmpty(failure))
                    {
                        error.AppendLine(failure);
                    }

                    return false;
                }

                if (!CommitGradePlans(grade, gradePlans, out failure))
                {
                    if (!string.IsNullOrEmpty(failure))
                    {
                        error.AppendLine(failure);
                    }

                    return false;
                }

                return true;
            }

            private bool TryBuildGradePlans(int grade, List<AIExamModelClass> gradeClasses, HashSet<int>? avoidRooms, out Dictionary<int, List<ClassRoomSlice>> gradePlans, out string? failure)
            {
                var availableRooms = GetAvailableRoomsForGrade(grade);
                if (avoidRooms != null && avoidRooms.Count > 0)
                {
                    var filtered = availableRooms.Where(r => !avoidRooms.Contains(r.RoomId)).ToList();
                    if (filtered.Count > 0 && TryBuildGradePlansForRooms(grade, gradeClasses, filtered, out gradePlans, out failure))
                    {
                        return true;
                    }
                }

                return TryBuildGradePlansForRooms(grade, gradeClasses, availableRooms, out gradePlans, out failure);
            }

            private List<RoomSnapshot> GetAvailableRoomsForGrade(int grade)
            {
                return _rooms
                    .Select(r => new
                    {
                        Room = r,
                        Available = GetAvailableSeatCount(r.ModelRoomId)
                    })
                    .Where(x => x.Available > 0
                        && RoomSupportsGrade(
                            x.Room.ModelRoomId,
                            grade,
                            _roomGradeUsage,
                            _roomSeatUsage,
                            _roomLookup))
                    .Select(x => new RoomSnapshot(x.Room, x.Available))
                    .ToList();
            }

            private int GetAvailableSeatCount(int roomId)
            {
                var capacity = _roomLookup.TryGetValue(roomId, out var room)
                    ? room.SeatCount
                    : 0;
                var used = _roomSeatUsage.TryGetValue(roomId, out var occupied)
                    ? occupied
                    : 0;
                var remaining = capacity - used;
                return remaining > 0 ? remaining : 0;
            }

            private bool TryBuildGradePlansForRooms(int grade, List<AIExamModelClass> gradeClasses, List<RoomSnapshot> rooms, out Dictionary<int, List<ClassRoomSlice>> gradePlans, out string? failure)
            {
                gradePlans = new Dictionary<int, List<ClassRoomSlice>>();
                failure = null;

                if (gradeClasses.Count == 0)
                {
                    return true;
                }

                if (rooms.Count == 0)
                {
                    failure = $"年级[{grade}]没有可用考场满足分配规则。";
                    return false;
                }

                var buildings = rooms
                    .GroupBy(r => r.BuildingId)
                    .Select(g => new BuildingRooms(
                        g.Key,
                        g.Select(x => new RoomSnapshot(
                                x.RoomId,
                                x.BuildingId,
                                x.RoomNo,
                                _roomLookup.TryGetValue(x.RoomId, out var info) ? info.SeatCount : x.Capacity,
                                x.AvailableSeats))
                            .OrderBy(x => x.RoomNo ?? int.MaxValue)
                            .ThenBy(x => x.AvailableSeats)
                            .ToList(),
                        g.Sum(x => x.AvailableSeats)))
                    .OrderByDescending(b => b.TotalSeats)
                    .ThenBy(b => b.BuildingId)
                    .ToList();

                var gradeUsedRooms = new HashSet<int>();

                foreach (var cls in gradeClasses.OrderByDescending(c => c.StudentCount).ThenBy(c => c.ModelClassId))
                {
                    var allocation = AllocateRoomsForClass(cls, buildings, gradeUsedRooms, null);
                    if (allocation == null || allocation.Count == 0)
                    {
                        failure = $"年级[{grade}]的班级[{cls.ModelClassName ?? cls.ModelClassId.ToString()}]无法找到满足规则的考场组合。";
                        return false;
                    }

                    gradePlans[cls.ModelClassId] = allocation;
                    foreach (var slice in allocation)
                    {
                        gradeUsedRooms.Add(slice.RoomId);
                    }
                }

                return true;
            }

            private bool CommitGradePlans(int grade, Dictionary<int, List<ClassRoomSlice>> gradePlans, out string? failure)
            {
                failure = null;

                foreach (var kvp in gradePlans)
                {
                    foreach (var slice in kvp.Value)
                    {
                        if (_roomLookup.TryGetValue(slice.RoomId, out var roomInfo))
                        {
                            var usedSeats = _roomSeatUsage.TryGetValue(slice.RoomId, out var seats)
                                ? seats
                                : 0;

                            if (slice.StudentCount + usedSeats > roomInfo.SeatCount)
                            {
                                var classLabel = _classLookup.TryGetValue(kvp.Key, out var classInfo)
                                    ? classInfo.ModelClassName ?? classInfo.ModelClassId.ToString()
                                    : kvp.Key.ToString();
                                failure = $"考场[{roomInfo.ModelRoomName ?? roomInfo.ModelRoomId.ToString()}]剩余座位不足，无法容纳班级[{classLabel}]。";
                                return false;
                            }
                        }

                        if (_roomGradeUsage.TryGetValue(slice.RoomId, out var grades)
                            && !grades.Contains(grade)
                            && grades.Count >= MaxGradesPerRoom)
                        {
                            failure = $"考场[{slice.RoomId}]被分配至超过允许的年级数量，违反规则。";
                            return false;
                        }
                    }
                }

                foreach (var kvp in gradePlans)
                {
                    _plans[kvp.Key] = kvp.Value.Select(slice => slice.Clone()).ToList();
                    foreach (var slice in kvp.Value)
                    {
                        if (!_roomGradeUsage.TryGetValue(slice.RoomId, out var grades))
                        {
                            grades = new HashSet<int>();
                            _roomGradeUsage[slice.RoomId] = grades;
                        }

                        grades.Add(grade);

                        var usedSeats = _roomSeatUsage.TryGetValue(slice.RoomId, out var seats)
                            ? seats
                            : 0;
                        _roomSeatUsage[slice.RoomId] = usedSeats + slice.StudentCount;
                    }
                }

                ClearSubjectOverridesForClasses(gradePlans.Keys);
                ClearSubjectBansForClasses(gradePlans.Keys);

                return true;
            }

            private void RemoveGradeAssignments(int grade)
            {
                var classIds = _classes.Where(c => c.NJ == grade).Select(c => c.ModelClassId).ToList();

                ClearSubjectOverridesForClasses(classIds);
                ClearSubjectBansForClasses(classIds);

                foreach (var classId in classIds)
                {
                    if (!_plans.TryGetValue(classId, out var plan))
                    {
                        continue;
                    }

                    foreach (var slice in plan)
                    {
                        if (_roomGradeUsage.TryGetValue(slice.RoomId, out var grades))
                        {
                            grades.Remove(grade);
                            if (grades.Count == 0)
                            {
                                _roomGradeUsage.Remove(slice.RoomId);
                            }
                        }

                        if (_roomSeatUsage.TryGetValue(slice.RoomId, out var seats))
                        {
                            seats -= slice.StudentCount;
                            if (seats <= 0)
                            {
                                _roomSeatUsage.Remove(slice.RoomId);
                            }
                            else
                            {
                                _roomSeatUsage[slice.RoomId] = seats;
                            }
                        }
                    }

                    _plans.Remove(classId);
                }
            }

            private Dictionary<int, Dictionary<int, HashSet<int>>> CloneSubjectBans()
            {
                return _subjectRoomBans.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToDictionary(inner => inner.Key, inner => new HashSet<int>(inner.Value)));
            }

            private Dictionary<int, List<ClassRoomSlice>> ClonePlans()
            {
                return _plans.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Select(slice => slice.Clone()).ToList());
            }

            private Dictionary<int, HashSet<int>> CloneUsage()
            {
                return _roomGradeUsage.ToDictionary(kvp => kvp.Key, kvp => new HashSet<int>(kvp.Value));
            }

            private Dictionary<int, int> CloneSeatUsage()
            {
                return _roomSeatUsage.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            private Dictionary<int, Dictionary<int, List<ClassRoomSlice>>> CloneOverrides()
            {
                return _subjectOverrides.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToDictionary(
                        inner => inner.Key,
                        inner => inner.Value.Select(slice => slice.Clone()).ToList()));
            }

            private Dictionary<int, List<ClassRoomSlice>> SnapshotGradePlans(int grade, Dictionary<int, List<ClassRoomSlice>> source)
            {
                var result = new Dictionary<int, List<ClassRoomSlice>>();
                foreach (var cls in _classes.Where(c => c.NJ == grade))
                {
                    if (source.TryGetValue(cls.ModelClassId, out var slices) && slices.Count > 0)
                    {
                        result[cls.ModelClassId] = slices.Select(slice => slice.Clone()).ToList();
                    }
                }

                return result;
            }

            private Dictionary<int, Dictionary<int, List<ClassRoomSlice>>> SnapshotGradeOverrides(int grade, Dictionary<int, Dictionary<int, List<ClassRoomSlice>>> source)
            {
                var result = new Dictionary<int, Dictionary<int, List<ClassRoomSlice>>>();
                var gradeClassIds = new HashSet<int>(_classes.Where(c => c.NJ == grade).Select(c => c.ModelClassId));

                foreach (var kvp in source)
                {
                    var filtered = kvp.Value
                        .Where(inner => gradeClassIds.Contains(inner.Key))
                        .ToDictionary(
                            inner => inner.Key,
                            inner => inner.Value.Select(slice => slice.Clone()).ToList());

                    if (filtered.Count > 0)
                    {
                        result[kvp.Key] = filtered;
                    }
                }

                return result;
            }

            private Dictionary<int, Dictionary<int, HashSet<int>>> SnapshotGradeBans(int grade, Dictionary<int, Dictionary<int, HashSet<int>>> source)
            {
                var result = new Dictionary<int, Dictionary<int, HashSet<int>>>();
                var gradeClassIds = new HashSet<int>(_classes.Where(c => c.NJ == grade).Select(c => c.ModelClassId));

                foreach (var kvp in source)
                {
                    var filtered = kvp.Value
                        .Where(inner => gradeClassIds.Contains(inner.Key))
                        .ToDictionary(inner => inner.Key, inner => new HashSet<int>(inner.Value));

                    if (filtered.Count > 0)
                    {
                        result[kvp.Key] = filtered;
                    }
                }

                return result;
            }

            private void RestorePlans(Dictionary<int, List<ClassRoomSlice>> backup)
            {
                _plans.Clear();
                foreach (var kvp in backup)
                {
                    _plans[kvp.Key] = kvp.Value.Select(slice => slice.Clone()).ToList();
                }
            }

            private void RestoreUsage(Dictionary<int, HashSet<int>> backup)
            {
                _roomGradeUsage.Clear();
                foreach (var kvp in backup)
                {
                    _roomGradeUsage[kvp.Key] = new HashSet<int>(kvp.Value);
                }
            }

            private void RestoreSeatUsage(Dictionary<int, int> backup)
            {
                _roomSeatUsage.Clear();
                foreach (var kvp in backup)
                {
                    _roomSeatUsage[kvp.Key] = kvp.Value;
                }
            }

            private void RestoreOverrides(Dictionary<int, Dictionary<int, List<ClassRoomSlice>>> backup)
            {
                _subjectOverrides.Clear();
                foreach (var kvp in backup)
                {
                    _subjectOverrides[kvp.Key] = kvp.Value.ToDictionary(
                        inner => inner.Key,
                        inner => inner.Value.Select(slice => slice.Clone()).ToList());
                }
            }

            private void RestoreSubjectBans(Dictionary<int, Dictionary<int, HashSet<int>>> backup)
            {
                _subjectRoomBans.Clear();
                foreach (var kvp in backup)
                {
                    _subjectRoomBans[kvp.Key] = kvp.Value.ToDictionary(
                        inner => inner.Key,
                        inner => new HashSet<int>(inner.Value));
                }
            }

            private void RestoreGradeAssignments(int grade, Dictionary<int, List<ClassRoomSlice>> originalPlans)
            {
                RemoveGradeAssignments(grade);

                foreach (var kvp in originalPlans)
                {
                    var clones = kvp.Value.Select(slice => slice.Clone()).ToList();
                    if (clones.Count == 0)
                    {
                        _plans.Remove(kvp.Key);
                        continue;
                    }

                    _plans[kvp.Key] = clones;

                    foreach (var slice in clones)
                    {
                        if (!_roomGradeUsage.TryGetValue(slice.RoomId, out var grades))
                        {
                            grades = new HashSet<int>();
                            _roomGradeUsage[slice.RoomId] = grades;
                        }

                        grades.Add(grade);

                        var usedSeats = _roomSeatUsage.TryGetValue(slice.RoomId, out var seats)
                            ? seats
                            : 0;
                        _roomSeatUsage[slice.RoomId] = usedSeats + slice.StudentCount;
                    }
                }
            }

            private void RestoreGradeOverrides(int grade, Dictionary<int, Dictionary<int, List<ClassRoomSlice>>> originalOverrides)
            {
                if (originalOverrides == null || originalOverrides.Count == 0)
                {
                    return;
                }

                foreach (var kvp in originalOverrides)
                {
                    if (!_subjectOverrides.TryGetValue(kvp.Key, out var subjectMap))
                    {
                        subjectMap = new Dictionary<int, List<ClassRoomSlice>>();
                        _subjectOverrides[kvp.Key] = subjectMap;
                    }

                    foreach (var classOverride in kvp.Value)
                    {
                        subjectMap[classOverride.Key] = classOverride.Value.Select(slice => slice.Clone()).ToList();
                    }
                }
            }

            private void RestoreGradeBans(int grade, Dictionary<int, Dictionary<int, HashSet<int>>> originalBans)
            {
                if (originalBans == null || originalBans.Count == 0)
                {
                    return;
                }

                foreach (var kvp in originalBans)
                {
                    if (!_subjectRoomBans.TryGetValue(kvp.Key, out var subjectMap))
                    {
                        subjectMap = new Dictionary<int, HashSet<int>>();
                        _subjectRoomBans[kvp.Key] = subjectMap;
                    }

                    foreach (var classBan in kvp.Value)
                    {
                        subjectMap[classBan.Key] = new HashSet<int>(classBan.Value);
                    }
                }
            }

            private static bool ArePlansEqual(List<ClassRoomSlice>? current, List<ClassRoomSlice>? previous)
            {
                if (current == null || current.Count == 0)
                {
                    return previous == null || previous.Count == 0;
                }

                if (previous == null || previous.Count == 0)
                {
                    return false;
                }

                if (current.Count != previous.Count)
                {
                    return false;
                }

                for (var i = 0; i < current.Count; i++)
                {
                    var a = current[i];
                    var b = previous[i];
                    if (a.RoomId != b.RoomId || a.StudentCount != b.StudentCount || a.SeatCount != b.SeatCount)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private sealed class ClassRoomSlice
        {
            public ClassRoomSlice(int roomId, int studentCount, int seatCount)
            {
                RoomId = roomId;
                StudentCount = studentCount;
                SeatCount = seatCount;
            }

            public int RoomId { get; }
            public int StudentCount { get; }
            public int SeatCount { get; }

            public ClassRoomSlice Clone() => new ClassRoomSlice(RoomId, StudentCount, SeatCount);
        }

        private sealed class RoomSnapshot
        {
            public RoomSnapshot(AIExamModelRoom room, int? availableSeats = null)
            {
                RoomId = room.ModelRoomId;
                BuildingId = room.BuildingId;
                RoomNo = room.RoomNo;
                Capacity = room.SeatCount;
                AvailableSeats = availableSeats ?? room.SeatCount;
            }

            public RoomSnapshot(int roomId, int buildingId, int? roomNo, int capacity, int availableSeats)
            {
                RoomId = roomId;
                BuildingId = buildingId;
                RoomNo = roomNo;
                Capacity = capacity;
                AvailableSeats = availableSeats;
            }

            public int RoomId { get; }
            public int BuildingId { get; }
            public int? RoomNo { get; }
            public int Capacity { get; }
            public int AvailableSeats { get; }
        }

        private sealed class BuildingRooms
        {
            public BuildingRooms(int buildingId, List<RoomSnapshot> rooms, int totalSeats)
            {
                BuildingId = buildingId;
                Rooms = rooms;
                TotalSeats = totalSeats;
            }

            public int BuildingId { get; }
            public List<RoomSnapshot> Rooms { get; }
            public int TotalSeats { get; }
        }

        private sealed class SubjectSessionItem
        {
            public SubjectSessionItem(
                AIExamModelSubject subject,
                ExamSession session,
                List<AIExamModelClass> classes,
                DateTime? forcedStart,
                int groupId)
            {
                Subject = subject;
                Session = session;
                Classes = classes;
                ForcedStart = forcedStart;
                GroupId = groupId;
            }

            public AIExamModelSubject Subject { get; }
            public ExamSession Session { get; }
            public List<AIExamModelClass> Classes { get; }
            public DateTime? ForcedStart { get; }
            public int GroupId { get; }
        }

        private sealed class ScheduledGroup
        {
            public ScheduledGroup(int groupId, List<SubjectSessionItem> subjects)
            {
                GroupId = groupId;
                Subjects = subjects;
                var firstForced = subjects
                    .Select(s => s.ForcedStart)
                    .FirstOrDefault(s => s.HasValue);

                HasForcedStart = firstForced.HasValue;
                ForcedStart = firstForced;
                MaxDuration = TimeSpan.FromMinutes(subjects.Max(s => Math.Max(1, s.Subject.Duration)));
                MaxPriority = subjects.Max(s => Math.Max(1, s.Subject.Priority));
            }

            public int GroupId { get; }
            public List<SubjectSessionItem> Subjects { get; }
            public bool HasForcedStart { get; }
            public DateTime? ForcedStart { get; }
            public TimeSpan MaxDuration { get; }
            public int MaxPriority { get; }
        }

        private sealed class RoomRequirement
        {
            public RoomRequirement(int roomId, int durationMinutes)
            {
                RoomId = roomId;
                DurationMinutes = Math.Max(1, durationMinutes);
            }

            public int RoomId { get; }
            public int DurationMinutes { get; }
        }

        private sealed class SubjectSchedule
        {
            public SubjectSchedule(ExamSession session, DateTime start, DateTime end)
            {
                SessionKey = session.SessionKey;
                DateText = session.DateText;
                Start = start;
                End = end;
            }

            public string SessionKey { get; }
            public string DateText { get; }
            public DateTime Start { get; }
            public DateTime End { get; }
        }

        private sealed class TeacherState
        {
            public TeacherState(AIExamModelTeacher teacher)
            {
                Teacher = teacher;
                AssignmentCount = 0;
                DailyRooms = new Dictionary<string, int>();
            }

            public AIExamModelTeacher Teacher { get; }
            public int AssignmentCount { get; set; }
            public Dictionary<string, int> DailyRooms { get; }
        }

        private sealed class Interval
        {
            public Interval(DateTime start, DateTime end)
            {
                Start = start;
                End = end;
            }

            public DateTime Start { get; }
            public DateTime End { get; }
        }

        #endregion
    }

    #region 数据模型定义

    public interface ITeacherRule
    {
        int TeacherId { get; }
        int TargetId { get; }
    }

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
        public int MaxStudentDaily { get; set; } = 0;
        public int MinExamInterval { get; set; } = 10;
    }

    public class AIExamModel
    {
        public AIExamConfig? Config { get; set; }
        public List<AIExamModelTime>? ModelTimeList { get; set; }
        public List<AIExamModelClass>? ModelClassList { get; set; }
        public List<AIExamModelRoom>? ModelRoomList { get; set; }
        public List<AIExamModelSubject>? ModelSubjectList { get; set; }
        public List<AIExamModelTeacher>? ModelTeacherList { get; set; }
        public List<AIExamRuleJointSubject>? RuleJointSubjectList { get; set; }
        public List<AIExamRuleJointSubjectNot>? RuleJointSubjectNotList { get; set; }
        public List<AIExamRuleRoomSubject>? RuleRoomSubjectList { get; set; }
        public List<AIExamRuleRoomSubject>? RuleRoomSubjectNotList { get; set; }
        public List<AIExamRuleTeacherBuilding>? RuleTeacherBuildingList { get; set; }
        public List<AIExamRuleTeacherBuilding>? RuleTeacherBuildingNotList { get; set; }
        public List<AIExamRuleTeacherClass>? RuleTeacherClassList { get; set; }
        public List<AIExamRuleTeacherClass>? RuleTeacherClassNotList { get; set; }
        public List<AIExamRuleTeacherSubject>? RuleTeacherSubjectList { get; set; }
        public List<AIExamRuleTeacherSubject>? RuleTeacherSubjectNotList { get; set; }
        public List<AIExamRuleTeacherUnTime>? RuleTeacherUnTimeList { get; set; }
    }

    public class AIExamModelTime
    {
        public string? Date { get; set; }
        public string? TimeNo { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
    }

    public class AIExamModelClass
    {
        public int NJ { get; set; }
        public int ModelClassId { get; set; }
        public string? ModelClassName { get; set; }
        public int StudentCount { get; set; }
    }

    public class AIExamModelRoom
    {
        public int ModelRoomId { get; set; }
        public string? ModelRoomName { get; set; }
        public int BuildingId { get; set; }
        public string? ExamMode { get; set; }
        public int Priority { get; set; } = 5;
        public int? RoomNo { get; set; }
        public int SeatCount { get; set; }
        public int TeacherCount { get; set; }
    }

    public class AIExamModelSubject
    {
        public int ModelSubjectId { get; set; }
        public string? ModelSubjectName { get; set; }
        public string? ExamMode { get; set; }
        public string? Date { get; set; }
        public string? StartTime { get; set; }
        public int Duration { get; set; }
        public int Difficulty { get; set; }
        public int Priority { get; set; } = 1;
        public List<AIExamModelSubjectClass>? ModelSubjectClassList { get; set; }
    }

    public class AIExamModelSubjectClass
    {
        public int ModelClassId { get; set; }
    }

    public class AIExamModelTeacher
    {
        public int ModelTeacherId { get; set; }
        public string? ModelTeacherName { get; set; }
        public int Gender { get; set; }
    }

    public class AIExamRuleJointSubject
    {
        public string? Date { get; set; }
        public string? StartTime { get; set; }
        public List<AIExamRuleJointSubjectItem>? RuleJointSubjectList { get; set; }
    }

    public class AIExamRuleJointSubjectItem
    {
        public int ModelSubjectId { get; set; }
    }

    public class AIExamRuleJointSubjectNot
    {
        public List<AIExamRuleJointSubjectItem>? RuleJointSubjectList { get; set; }
    }

    public class AIExamRuleRoomSubject
    {
        public int ModelSubjectId { get; set; }
        public int ModelRoomId { get; set; }
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
        public int ModelTeacherId { get; set; }
        public string? Date { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
    }

    #endregion
}