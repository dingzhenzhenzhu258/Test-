using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Models
{
    /// <summary>
    /// 通用通讯返回值基类
    /// </summary>
    public class OperateResult
    {
        /// <summary>
        ///  是否成功
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// 返回的消息
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// 返回的错误码
        /// </summary>
        public int ErrorCode { get; set; }

        public OperateResult()
        {

        }

        public OperateResult(bool IsSuccess, string Message, int ErrorCode = 0)
        {
            this.IsSuccess = IsSuccess;
            this.Message = Message;
            this.ErrorCode = ErrorCode;
        }
    }

    /// <summary>
    /// 不同数据类型的返回值
    /// </summary>
    /// <typeparam name="T">数据类型</typeparam>
    public class OperateResult<T> : OperateResult
    {
        /// <summary>
        /// 返回值
        /// </summary>
        public T? Content { get; set; }

        public OperateResult(T? Content, bool IsSuccess, string Message, int ErrorCode = 0) : base(IsSuccess, Message, ErrorCode)
        {
            this.Content = Content;
        }

        public OperateResult(bool IsSuccess, string Message, int ErrorCode = 0) : base(IsSuccess, Message, ErrorCode)
        {
            this.Content = Content;
        }
    }
}
