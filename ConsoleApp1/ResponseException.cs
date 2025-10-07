using DTcms.Core.Common.Emums;

namespace DTcms.Core.Common.Helpers
{
    [Serializable]
    public class ResponseException : ApplicationException
    {
        /// <summary>
        /// 状态码
        /// </summary>
        private readonly int _code;

        /// <summary>
        /// 错误码，当为0时，代表正常
        /// </summary>
        private readonly ErrorCode _errorCode;

        /// <summary>
        /// 构造函数
        /// </summary>
        public ResponseException() : base("服务器繁忙，请稍后再试!")
        {
            _errorCode = ErrorCode.Fail;
            _code = 400;
        }

        public ResponseException(string? message = "服务器繁忙，请稍后再试!", ErrorCode errorCode = ErrorCode.Fail, int code = 400) : base(message)
        {
            this._errorCode = errorCode;
            _code = code;
        }

        /// <summary>
        /// 浏览器响应码
        /// </summary>
        public int GetCode()
        {
            return _code;
        }

        /// <summary>
        /// 自定义错误码
        /// </summary>
        public ErrorCode GetErrorCode()
        {
            return _errorCode;
        }
    }
}