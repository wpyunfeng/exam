namespace DTcms.Core.Common.Emums
{
    public enum ErrorCode
    {
        /// <summary>
        /// 操作成功
        /// </summary>
        Success = 200,
        /// <summary>
        /// 操作失败
        /// </summary>
        Fail = 400,
        /// <summary>
        /// 资源不存在
        /// </summary>
        NotFound = 404,
        /// <summary>
        /// 认证失败
        /// </summary>
        AuthFailed = 401,
        /// <summary>
        /// 已登录但无权限
        /// </summary>
        NoPermission = 407,
        /// <summary>
        /// 参数错误
        /// </summary>
        ParamError = 422,
        /// <summary>
        /// 令牌过期
        /// </summary>
        TokenExpired = 403,
        /// <summary>
        /// 字段重复
        /// </summary>
        RepeatField = 416,
        /// <summary>
        /// 禁止操作
        /// </summary>
        Inoperable = 405,
        /// <summary>
        /// 服务器错误
        /// </summary>
        Error = 500,
        /// <summary>
        /// 请求过于频繁，请稍后重试
        /// </summary>
        ManyRequests = 503
    }
}