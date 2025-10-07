using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Text;

namespace DTcms.Core.Common.Helpers
{
    /// <summary>
    /// JSON格式转换帮助类
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// 对象转换为JSON字符串
        /// </summary>
        public static string GetJson(object obj)
        {
            return JsonConvert.SerializeObject(obj);
        }

        /// <summary>
        /// JSON字符转换为T类型的对象
        /// </summary>
        public static T? ToJson<T>(string jsonStr)
        {
            if (string.IsNullOrEmpty(jsonStr)) return default;

            try
            {
                return JsonConvert.DeserializeObject<T>(jsonStr);
            }
            catch (JsonException ex)
            {
                return default;
            }
        }

        /// <summary>
        /// 对象转换为JSON字符串
        /// </summary>
        public static string ToJson(this object obj)
        {
            JsonSerializerSettings settings = new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,//忽略循环引用，如果设置为Error，则遇到循环引用的时候报错（建议设置为Error，这样更规范）
                NullValueHandling = NullValueHandling.Ignore,//忽略值NULL的属性
                DateFormatString = "yyyy-MM-dd HH:mm:ss",//日期格式化，默认的格式也不好看
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()//json中属性开头字母小写的驼峰命名
            };
            return JsonConvert.SerializeObject(obj, settings);
        }

        /// <summary>
        /// JSON字符串转换为T对象
        /// </summary>
        public static T? ToObject<T>(this string? jsonStr)
        {
            return jsonStr == null ? default : JsonConvert.DeserializeObject<T>(jsonStr);
        }

        public static T? ToObject<T>(this object obj)
        {
            string jsonStr = ToJson(obj);
            return jsonStr == null ? default(T) : JsonConvert.DeserializeObject<T>(jsonStr);
        }

        /// <summary>
        /// 将对象序列化为字节数组
        /// </summary>
        public static byte[] Serialize(object obj)
        {
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),  //使用驼峰样式
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            };
            var jsonString = JsonConvert.SerializeObject(obj, settings);
            return Encoding.UTF8.GetBytes(jsonString);
        }

        /// <summary>
        /// 将字节数组反序列化为对象
        /// </summary>
        public static T? Deserialize<T>(byte[] value)
        {
            if (value == null)
            {
                return default(T);
            }
            var jsonString = Encoding.UTF8.GetString(value);
            return JsonConvert.DeserializeObject<T>(jsonString);
        }
    }
}