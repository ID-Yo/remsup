using System.Text;
internal static class RuntimeSetup {
 internal static void Initialize(){Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);}
}
