using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wpf.Ui.Abstractions.Controls;

namespace Test_High_speed_acquisition.ViewModels.Models
{
    /// <summary>
    /// 视图模型基类，封装页面导航生命周期方法。
    /// </summary>
    public abstract class ViewModel : ObservableValidator, INavigationAware
    {
        // 添加序列化/反序列化方法
        /// <summary>
        /// 序列化
        /// </summary>
        /// <returns></returns>
        public virtual JObject Serialize()
        {
            // 只序列化标记了[JsonProperty]的属性
            return JObject.FromObject(this);
        }

        /// <summary>
        /// 将传入的 JObject 数据反序列化，填充到当前对象实例中
        /// </summary>
        /// <param name="data"></param>
        public virtual void Deserialize(JObject data)
        {
            JsonConvert.PopulateObject(data.ToString(), this);
        }

        // 添加虚拟方法用于子类自定义序列化
        /// <summary>
        /// 在对象被序列化之前调用
        /// </summary>
        public virtual void BeforeSerialize() { }

        /// <summary>
        /// 在对象被反序列化之后调用
        /// </summary>
        public virtual void AfterDeserialize() { }

        /// <inheritdoc />
        public virtual Task OnNavigatedToAsync()
        {
            OnNavigatedTo();

            return Task.CompletedTask;
        }

        /// <summary>
        /// 在组件导航到当前页面后触发。
        /// </summary>
        public virtual void OnNavigatedTo() { }

        /// <inheritdoc />
        public virtual Task OnNavigatedFromAsync()
        {
            OnNavigatedFrom();

            return Task.CompletedTask;
        }

        /// <summary>
        /// 在组件从当前页面导航离开前触发。
        /// </summary>
        public virtual void OnNavigatedFrom() { }
    }
}
