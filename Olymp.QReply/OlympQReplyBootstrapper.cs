using System;
using System.Collections.Generic;
using System.Windows;

using Caliburn.Micro;

using Olymp.QReply.Windows;

using SimpleContainer = Caliburn.Micro.SimpleContainer;

namespace Olymp.QReply;

internal class OlympQReplyBootstrapper : BootstrapperBase
{
    private SimpleContainer? m_Ioc;

    public OlympQReplyBootstrapper()
    {
        Initialize();
    }

    protected override void PrepareApplication()
    {
        base.PrepareApplication();
        /// Uncomment if needed.
        //LogManager.GetLog = (type) => Logger.Default;
    }
    protected override void Configure()
    {
        base.Configure();
        m_Ioc = new SimpleContainer();
        InitContainer();
    }
    private void InitContainer()
    {
        m_Ioc!.RegisterSingleton(typeof(IAppService), null, typeof(AppService));
        m_Ioc.RegisterSingleton(typeof(IWindowManager), null, typeof(WindowManager));
        m_Ioc.RegisterSingleton(typeof(IEventAggregator), null, typeof(EventAggregator));
        m_Ioc.RegisterSingleton(typeof(OlympDataContext), null, typeof(OlympDataContext));
        m_Ioc.RegisterSingleton(typeof(BrowseReplyViewModel), null, typeof(BrowseReplyViewModel));
        m_Ioc.RegisterSingleton(typeof(IMessageBoxService), null, typeof(MessageBoxService));
    }
    protected override void BuildUp(object instance)
    {
        m_Ioc!.BuildUp(instance);
    }
    protected override object GetInstance(Type service, string key)
    {
        return m_Ioc!.GetInstance(service, key);
    }
    protected override IEnumerable<object> GetAllInstances(Type service)
    {
        return m_Ioc!.GetAllInstances(service);
    }
    protected override void OnUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        base.OnUnhandledException(sender, e);
        LogManager.GetLog(GetType()).Error(e.Exception);
        IoC.Get<IMessageBoxService>().ShowMessage("Необработанное исключение. \nПриложение будет закрыто.");
        e.Handled = true;
        Application.Shutdown(1);
    }

    protected override void OnStartup(object sender, StartupEventArgs e)
    {
        DisplayRootViewForAsync<BrowseReplyViewModel>();
    }
}
