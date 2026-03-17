namespace SerialPortService.Models
{
    /// <summary>
    /// 通用通讯返回值基类
    /// </summary>
    public class OperateResult
    {
        /// <summary>
        /// 指示操作是否成功。
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// 操作结果说明。
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// 业务或协议错误码。
        /// </summary>
        public int ErrorCode { get; set; }

        /// <summary>
        /// 创建空的操作结果。
        /// </summary>
        public OperateResult() { }

        /// <summary>
        /// 创建操作结果。
        /// </summary>
        /// <param name="isSuccess">是否成功</param>
        /// <param name="message">结果说明</param>
        /// <param name="errorCode">错误码</param>
        public OperateResult(bool isSuccess, string message, int errorCode = 0)
        {
            IsSuccess = isSuccess;
            Message = message;
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// 带泛型内容的通讯返回值
    /// </summary>
    public class OperateResult<T> : OperateResult
    {
        /// <summary>
        /// 返回内容。
        /// </summary>
        public T? Content { get; set; }

        /// <summary>
        /// 创建带返回内容的操作结果。
        /// </summary>
        /// <param name="content">返回内容</param>
        /// <param name="isSuccess">是否成功</param>
        /// <param name="message">结果说明</param>
        /// <param name="errorCode">错误码</param>
        public OperateResult(T? content, bool isSuccess, string message, int errorCode = 0)
            : base(isSuccess, message, errorCode)
        {
            Content = content;
        }

        /// <summary>
        /// 创建不含返回内容的操作结果。
        /// </summary>
        /// <param name="isSuccess">是否成功</param>
        /// <param name="message">结果说明</param>
        /// <param name="errorCode">错误码</param>
        public OperateResult(bool isSuccess, string message, int errorCode = 0)
            : base(isSuccess, message, errorCode)
        {
        }
    }
}
