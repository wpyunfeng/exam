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
        public static List<AIExamClassRoomResult> AutoClassRoom(AIExamClassRoomModel model)
        {
            List<AIExamClassRoomResult> result = new List<AIExamClassRoomResult>();
            return result;
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