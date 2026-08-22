using System.Reflection;
using BrowserServer;

namespace BrowserServer.Tests.Helpers
{
    /// <summary>
    /// Invokes StreamingDownloadHandler without compiling against CefSharp.DownloadHandler.
    /// </summary>
    public static class DownloadHandlerReflection
    {
        static readonly MethodInfo HandleClientAckMethod =
            typeof(DeviceContextHub).Assembly
                .GetType("BrowserServer.StreamingDownloadHandler")
                .GetMethod("HandleClientAck", BindingFlags.Public | BindingFlags.Static);

        public static void HandleClientAck(string id, int seq)
        {
            HandleClientAckMethod.Invoke(null, new object[] { id, seq });
        }
    }
}
