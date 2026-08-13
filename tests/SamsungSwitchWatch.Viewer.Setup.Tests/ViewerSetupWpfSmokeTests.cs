using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using SamsungSwitchWatch.Viewer.Setup.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ViewerSetupWpfSmokeCollection
{
    public const string Name = "Viewer Setup WPF smoke";
}

[Collection(ViewerSetupWpfSmokeCollection.Name)]
public sealed class ViewerSetupWpfSmokeTests
{
    [Fact]
    public void MainWindow_SupportCodeIsHiddenByDefaultAndFitsMinimumViewport()
    {
        RunOnSta(() =>
        {
            using var workspace = new TestWorkspace();
            var app = new App();
            app.InitializeComponent();
            var window = new MainWindow(workspace.CreateOrchestrator())
            {
                Width = 620,
                Height = 500
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, window.SupportCodePanel.Visibility);
                Assert.Empty(window.SupportCodeTextBox.Text);
                Assert.True(window.SupportCodeTextBox.IsReadOnly);
                Assert.Equal(
                    "Viewer 설치 지원 코드",
                    AutomationProperties.GetName(window.SupportCodeTextBox));

                window.PresentSupportCode(CreateSnapshot(), shouldShow: true);
                window.UpdateLayout();

                Assert.Equal(Visibility.Visible, window.SupportCodePanel.Visibility);
                Assert.Matches(
                    "^SWS1-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$",
                    window.SupportCodeTextBox.Text);
                Assert.True(window.SupportCodePanel.ActualHeight > 0);
                Assert.True(window.CopySupportCodeButton.IsVisible);
                Assert.True(window.CloseButton.IsVisible);

                window.HideSupportCode();
                Assert.Equal(Visibility.Collapsed, window.SupportCodePanel.Visibility);
                Assert.Empty(window.SupportCodeTextBox.Text);
            }
            finally
            {
                window.Close();
                app.Shutdown();
            }
        });
    }

    private static ViewerSetupDiagnosticSnapshot CreateSnapshot() =>
        ViewerSetupDiagnosticFactory.Create(
            "0.11.10-poc",
            ViewerSetupDiagnosticOperation.Install,
            "VIEWER_SETUP_SMOKE_FAILED",
            failedStage: ViewerSetupDiagnosticStage.Smoke,
            stages: ViewerSetupDiagnosticStageStates.Empty with
            {
                Package = ViewerSetupDiagnosticStageState.Succeeded,
                Smoke = ViewerSetupDiagnosticStageState.Failed
            });

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF smoke test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
