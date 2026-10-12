using System;
internal static class TestRunner {
 [STAThread] public static int Main() {
  RelayShutdownRaceTests.Run();
  ScreenSessionTests.Run();
  ScreenInputTests.Run();
  ScreenCaptureTests.Run();
  try { System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException); if(Environment.GetEnvironmentVariable("REMSUP_CAPTURE_WORKSPACE_ONLY")=="1"){WorkspaceUiTests.Run();Console.WriteLine("PASS focused native workspace capture");return 0;}ImageLifetimeTests.Run(); SessionWorkspaceTests.Run(); CurrentStoreTests.Run(); RelayProfileTests.Run(); RelayHelpUiTests.Run(); AutomaticSupportTests.Run(); AppearanceTests.Run(); FriendlyUiTests.Run(); ClipboardTests.Run(); TransportTests.Run(); LanSupportTests.Run(); DeviceStoreTests.Run(); OperatorConnectionTests.Run(); RelayIntegrationTests.Run(); UiTests.Run(); Console.WriteLine("PASS Windows tests"); return 0; }
  catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
 }
}
