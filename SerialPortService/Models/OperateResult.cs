namespace SerialPortService.Models
{
    /// <summary>
    /// 通用通讯返回值基类
    /// </summary>
    public class OperateResult
    {
        public bool IsSuccess { get; set; }
        public string? Message { get; set; }
        public int ErrorCode { get; set; }

        public OperateResult() { }

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
        public T? Content { get; set; }

        public OperateResult(T? content, bool isSuccess, string message, int errorCode = 0)
            : base(isSuccess, message, errorCode)
        {
            Content = content;
        }

        public OperateResult(bool isSuccess, string message, int errorCode = 0)
            : base(isSuccess, message, errorCode)
        {
        }
    }
}
