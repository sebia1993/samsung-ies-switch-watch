using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using SamsungSwitchWatch.Viewer.Setup.Deployment;
using SamsungSwitchWatch.Viewer.Setup.Diagnostics;

namespace SamsungSwitchWatch.Viewer.Setup;

public partial class MainWindow : Window
{
    private readonly ViewerDeploymentOrchestrator _orchestrator;
    private CancellationTokenSource? _operationCancellation;
    private bool _busy;

    public MainWindow(ViewerDeploymentOrchestrator orchestrator)
    {
        InitializeComponent();
        _orchestrator = orchestrator;
        Loaded += (_, _) => RefreshRecoveryState();
        Closing += OnClosing;
    }

    private async void InstallButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            ViewerSetupOperationKind.Install,
            "Viewer를 설치하고 있습니다...",
            token => _orchestrator.DeployAsync(token));
    }

    private async void RecoverButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            ViewerSetupOperationKind.Recovery,
            "이전 Viewer 상태를 복구하고 있습니다...",
            token => _orchestrator.RecoverAsync(token));
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private async Task RunOperationAsync(
        ViewerSetupOperationKind operationKind,
        string progressMessage,
        Func<CancellationToken, Task<ViewerSetupResult>> operation)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _operationCancellation = new CancellationTokenSource();
        HideSupportCode();
        SetButtonsEnabled(false);
        SetStatus("작업 중", progressMessage, "", "#2563EB");
        StepList.ItemsSource = null;
        try
        {
            var result = await Task.Run(
                () => operation(_operationCancellation.Token));
            StepList.ItemsSource = result.Steps;
            var presentation = ViewerSetupUiPolicy.Result(operationKind, result);
            SetStatus(
                presentation.Title,
                presentation.Message,
                result.Succeeded ? string.Empty : $"Cause: {result.Code}",
                result.Succeeded ? "#16A34A" : "#DC2626");
            PresentSupportCode(
                result.Diagnostic ?? CreateFallbackDiagnostic(operationKind, result.Code),
                ViewerSetupUiPolicy.ShouldShowSupportCode(result));
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _busy = false;
            RefreshRecoveryState(updateStatus: false);
            SetButtonsEnabled(true);
        }
    }

    private void RefreshRecoveryState(bool updateStatus = true)
    {
        var recovery = _orchestrator.InspectPendingRecovery();
        var state = ViewerSetupUiPolicy.Buttons(_busy, recovery);
        InstallButton.IsEnabled = state.InstallEnabled;
        RecoverButton.IsEnabled = state.RecoverEnabled;
        CloseButton.IsEnabled = state.CloseEnabled;
        if (updateStatus && recovery.Exists)
        {
            SetStatus(
                "이전 작업 확인 필요",
                recovery.Message,
                $"Cause: {recovery.Code}",
                "#D97706");
            PresentSupportCode(
                recovery.Diagnostic ?? CreateFallbackRecoveryDiagnostic(recovery.Code),
                ViewerSetupUiPolicy.ShouldShowSupportCode(recovery));
        }
        else if (updateStatus)
        {
            HideSupportCode();
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        var recovery = _orchestrator.InspectPendingRecovery();
        var state = ViewerSetupUiPolicy.Buttons(!enabled, recovery);
        InstallButton.IsEnabled = state.InstallEnabled;
        CloseButton.IsEnabled = state.CloseEnabled;
        RecoverButton.IsEnabled = state.RecoverEnabled;
    }

    private void SetStatus(
        string title,
        string message,
        string code,
        string color)
    {
        StatusTitle.Text = title;
        StatusMessage.Text = message;
        StatusCode.Text = code;
        StatusCode.Visibility = string.IsNullOrWhiteSpace(code)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusIndicator.Fill =
            (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    }

    internal void PresentSupportCode(
        ViewerSetupDiagnosticSnapshot? diagnostic,
        bool shouldShow)
    {
        var supportCode = shouldShow
            ? ViewerSetupSupportCodePolicy.TryCreate(diagnostic)
            : null;
        if (string.IsNullOrWhiteSpace(supportCode))
        {
            HideSupportCode();
            return;
        }

        SupportCodeTextBox.Text = supportCode;
        SupportCodeCopyFeedbackText.Text = string.Empty;
        SupportCodeCopyFeedbackText.Visibility = Visibility.Collapsed;
        SupportCodePanel.Visibility = Visibility.Visible;
    }

    internal void HideSupportCode()
    {
        SupportCodePanel.Visibility = Visibility.Collapsed;
        SupportCodeTextBox.Clear();
        SupportCodeCopyFeedbackText.Text = string.Empty;
        SupportCodeCopyFeedbackText.Visibility = Visibility.Collapsed;
    }

    private void CopySupportCodeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var feedback = TryCopySupportCode(SupportCodeTextBox.Text, Clipboard.SetText);
        SupportCodeCopyFeedbackText.Text = feedback;
        SupportCodeCopyFeedbackText.Visibility = Visibility.Visible;
    }

    internal static string TryCopySupportCode(string? supportCode, Action<string> copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (string.IsNullOrWhiteSpace(supportCode))
        {
            return "복사할 지원 코드가 없습니다.";
        }

        try
        {
            copy(supportCode);
            return "지원 코드를 복사했습니다.";
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException or InvalidOperationException)
        {
            return "복사하지 못했습니다. 코드를 선택한 뒤 Ctrl+C를 누르세요.";
        }
    }

    private static ViewerSetupDiagnosticSnapshot CreateFallbackDiagnostic(
        ViewerSetupOperationKind operation,
        string code) =>
        ViewerSetupDiagnosticFactory.Create(
            ProductVersion,
            operation == ViewerSetupOperationKind.Install
                ? ViewerSetupDiagnosticOperation.Install
                : ViewerSetupDiagnosticOperation.Recovery,
            code);

    private static ViewerSetupDiagnosticSnapshot CreateFallbackRecoveryDiagnostic(
        string code) =>
        ViewerSetupDiagnosticFactory.Create(
            ProductVersion,
            ViewerSetupDiagnosticOperation.RecoveryInspection,
            code,
            journalState: ViewerSetupDiagnosticJournalState.Unreadable);

    private static string ProductVersion =>
        typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ??
        "UNKNOWN";

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy)
        {
            return;
        }

        e.Cancel = true;
        _operationCancellation?.Cancel();
    }
}
