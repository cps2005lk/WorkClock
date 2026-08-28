using System.Windows.Forms;
using WorkClock;

ApplicationConfiguration.Initialize();
Application.Run(new TrayContext(AppConfig.Load()));
