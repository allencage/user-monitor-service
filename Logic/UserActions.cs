using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
// ReSharper disable InconsistentNaming

namespace Logic
{
    public static class UserActions
    {
        public enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive,
            WTSConnected,
            WTSConnectQuery,
            WTSShadow,
            WTSDisconnected,
            WTSIdle,
            WTSListen,
            WTSReset,
            WTSDown,
            WTSInit
        }

        private enum WtsInfoClass
        {
            WTSInitialProgram = 0,
            WTSApplicationName = 1,
            WTSWorkingDirectory = 2,
            WTSOEMId = 3,
            WTSSessionId = 4,
            WTSUserName = 5,
            WTSWinStationName = 6,
            WTSDomainName = 7,
            WTSConnectState = 8,
            WTSClientBuildNumber = 9,
            WTSClientName = 10,
            WTSClientDirectory = 11,
            WTSClientProductId = 12,
            WTSClientHardwareId = 13,
            WTSClientAddress = 14,
            WTSClientDisplay = 15,
            WTSClientProtocolType = 16
        }

        private static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

        private static readonly object lockObject = new object();

        private static readonly Dictionary<string, long> UserSession = new Dictionary<string, long>();

        [DllImport("Wtsapi32.dll")]
        private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WtsInfoClass wtsInfoClass,
            out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("Wtsapi32.dll")]
        private static extern int WTSEnumerateSessions(
            IntPtr hServer,
            [MarshalAs(UnmanagedType.U4)] int Reserved,
            [MarshalAs(UnmanagedType.U4)] int Version,
            ref IntPtr ppSessionInfo,
            [MarshalAs(UnmanagedType.U4)] ref int pCount
        );

        [DllImport("Wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr pointer);

        private static string GetProcessUser(Process process)
        {
            var processHandle = IntPtr.Zero;
            try
            {
                OpenProcessToken(process.Handle, 8, out processHandle);
                var wi = new WindowsIdentity(processHandle);
                var user = wi.Name;
                return user;
                //Note - this is the way the user is formed, that is why I left it commented
                //return user.Contains(@"\") ? user.Substring(user.IndexOf(@"\") + 1) : user;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (processHandle != IntPtr.Zero)
                    CloseHandle(processHandle);
            }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static string GetUsername(int sessionId, bool prependDomain = true)
        {
            var username = "SYSTEM";
            if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.WTSUserName, out var buffer,
                    out var strLen) && strLen > 1)
            {
                username = Marshal.PtrToStringAnsi(buffer);
                WTSFreeMemory(buffer);
                if (prependDomain)
                    if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.WTSDomainName, out buffer,
                            out strLen) && strLen > 1)
                    {
                        username = Marshal.PtrToStringAnsi(buffer) + "\\" + username;
                        WTSFreeMemory(buffer);
                    }
            }
            return username;
        }

        public static bool IsUserActive(string domainAndUsername)
        {
            var serverHandle = WTS_CURRENT_SERVER_HANDLE;
            var userIsActive = false;

            try
            {
                var sessionPtr = IntPtr.Zero;
                var sessionCount = 0;
                var sessRet = WTSEnumerateSessions(serverHandle, 0, 1, ref sessionPtr, ref sessionCount);
                var dataSize = (ulong) Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                var currentSession = (ulong) sessionPtr;

                if (sessRet == 0)
                    throw new ApplicationException("Unable to enumerate sessions.");
                for (var i = 0; i < sessionCount; i++)
                {
                    var si = (WTS_SESSION_INFO) Marshal.PtrToStructure((IntPtr) currentSession,
                        typeof(WTS_SESSION_INFO));
                    currentSession += dataSize;

                    if (si.State == WTS_CONNECTSTATE_CLASS.WTSActive ||
                        si.State == WTS_CONNECTSTATE_CLASS.WTSConnected)
                    {
                        int bytes;
                        WTSQuerySessionInformation(serverHandle, si.SessionID, WtsInfoClass.WTSUserName,
                            out var userPtr, out bytes);
                        WTSQuerySessionInformation(serverHandle, si.SessionID, WtsInfoClass.WTSDomainName,
                            out var domainPtr, out bytes);

                        var test = Marshal.PtrToStringAnsi(domainPtr) + "\\" + Marshal.PtrToStringAnsi(userPtr);
                        if (domainAndUsername != null &&
                            domainAndUsername.Equals(test, StringComparison.InvariantCultureIgnoreCase))
                            userIsActive = true;
                        WTSFreeMemory(userPtr);
                        WTSFreeMemory(domainPtr);
                    }
                }

                WTSFreeMemory(sessionPtr);
            }
            catch (Exception ex)
            {
                throw ex;
            }

            return userIsActive;
        }

        public static void LogUserAction(SessionChangeDescription changeDescription)
        {
            var userName = GetUsername(changeDescription.SessionId);
            if (userName.Contains("SYSTEM"))
                return;
            if (!UserSession.ContainsKey(userName))
                UserSession.Add(userName, 0);
            var domainAndUsername = userName.Split('\\');
            var machineName = Environment.MachineName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public int SessionID;
            [MarshalAs(UnmanagedType.LPStr)] public string pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }
    }
}