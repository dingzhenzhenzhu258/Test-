using CommunityToolkit.Mvvm.ComponentModel;
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
    public abstract class ViewModel : ObservableObject, INavigationAware
    {
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
