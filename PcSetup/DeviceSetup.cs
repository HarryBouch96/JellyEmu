// Helpers for the JellyEmu gaming PC setup script (setup.ps1), loaded with Add-Type.
//  - JeDriver: installs a root-enumerated driver (the Virtual Display Driver), like devcon does.
//  - JeHttp:   talks to the local bridge and Sunshine (Sunshine's own web API uses a self-signed
//              certificate, accepted for this PC only), including the bridge's two-step pairing.
using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

public static class JeDriver
{
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    const int DICD_GENERATE_ID = 0x1;
    const int SPDRP_HARDWAREID = 0x1;
    const int DIF_REGISTERDEVICE = 0x19;
    const int INSTALLFLAG_FORCE = 0x1;

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiCreateDeviceInfo(IntPtr set, string deviceName, ref Guid classGuid, string description,
        IntPtr hwndParent, int flags, ref SP_DEVINFO_DATA data);

    // Unicode: the W version. (Without it .NET calls the ANSI one, which reads the UTF-16 id as
    // single letters, so the device's hardware id came out as "R", "o", "o", "t", ...)
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int property, byte[] buffer, int size);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string infPath, int flags, out bool rebootRequired);

    static Exception Failed(string call)
    {
        int code = Marshal.GetLastWin32Error();
        return new InvalidOperationException(string.Format("{0} failed: 0x{1:X8} {2}", call, code, new Win32Exception(code).Message));
    }

    /// <summary>
    /// Creates a root-enumerated device with this hardware id (no driver yet: install one with
    /// pnputil /add-driver ... /install, which picks it up by the id).
    /// </summary>
    public static void CreateRootDevice(string hardwareId, string className, Guid classGuid)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw Failed("SetupDiCreateDeviceInfoList");
        try
        {
            var data = new SP_DEVINFO_DATA();
            data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            if (!SetupDiCreateDeviceInfo(set, className, ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref data))
                throw Failed("SetupDiCreateDeviceInfo");
            byte[] ids = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, ids, ids.Length))
                throw Failed("SetupDiSetDeviceRegistryProperty");
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
                throw Failed("SetupDiCallClassInstaller");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>
    /// Creates a root device with this hardware id and installs the driver from the .inf onto it.
    /// Returns true if Windows says a restart is needed.
    /// </summary>
    public static bool InstallRootDevice(string infPath, string hardwareId, string className, Guid classGuid)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfoList");
        try
        {
            var data = new SP_DEVINFO_DATA();
            data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            if (!SetupDiCreateDeviceInfo(set, className, ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfo");
            // REG_MULTI_SZ: the id, then an empty string.
            byte[] ids = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(set, ref data, SPDRP_HARDWAREID, ids, ids.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiSetDeviceRegistryProperty");
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCallClassInstaller");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out reboot))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateDriverForPlugAndPlayDevices");
        return reboot;
    }
}

public static class JeHttp
{
    public static CookieContainer NewCookies() { return new CookieContainer(); }

    /// <summary>
    /// When set, requests to the bridge (plain http) sign in as this account through the bridge's
    /// forwarded header, the way Jellyfin's stream proxy does.
    /// </summary>
    public static string BridgeUser;

    static bool IsLocal(Uri uri)
    {
        return uri.IsLoopback || uri.Host == "127.0.0.1" || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    static HttpWebRequest Build(string method, string url, CookieContainer cookies, string basicUser, string basicPassword, int timeoutMs)
    {
        var uri = new Uri(url);
        var request = (HttpWebRequest)WebRequest.Create(uri);
        request.Method = method;
        request.Timeout = timeoutMs;
        request.ReadWriteTimeout = timeoutMs;
        request.CookieContainer = cookies;
        request.Accept = "application/json";
        if (basicUser != null)
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(basicUser + ":" + basicPassword));
        if (BridgeUser != null && uri.Scheme == "http")
            request.Headers["X-JellyEmu-Stream-User"] = BridgeUser;
        // Sunshine's web API has a self-signed certificate. Accept it only for this PC itself.
        if (uri.Scheme == "https" && IsLocal(uri))
            request.ServerCertificateValidationCallback = delegate { return true; };
        return request;
    }

    static void WriteBody(HttpWebRequest request, string json)
    {
        if (json == null) return;
        byte[] body = Encoding.UTF8.GetBytes(json);
        request.ContentType = "application/json";
        request.ContentLength = body.Length;
        using (var s = request.GetRequestStream()) s.Write(body, 0, body.Length);
    }

    /// <summary>Sends a request and returns the response body. Throws with the status and body on errors.</summary>
    public static string Request(string method, string url, string json, CookieContainer cookies, string basicUser, string basicPassword)
    {
        var request = Build(method, url, cookies, basicUser, basicPassword, 30000);
        WriteBody(request, json);
        try
        {
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }
        catch (WebException e)
        {
            var response = e.Response as HttpWebResponse;
            if (response == null) throw;
            string text;
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) text = reader.ReadToEnd();
            throw new InvalidOperationException(method + " " + url + " failed: HTTP " + (int)response.StatusCode + " " + text);
        }
    }

    /// <summary>
    /// Pairs the bridge with Sunshine: asks the bridge to pair (it answers with a PIN, then waits),
    /// gives that PIN to Sunshine's web API, and returns the bridge's final answer.
    /// </summary>
    public static string Pair(string bridgeUrl, CookieContainer cookies, long hostId,
        string sunshineUrl, string sunshineUser, string sunshinePassword, string clientName)
    {
        var request = Build("POST", bridgeUrl.TrimEnd('/') + "/api/pair", cookies, null, null, 120000);
        WriteBody(request, "{\"host_id\":" + hostId + "}");
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
        {
            string first = reader.ReadLine();
            if (first == null) throw new InvalidOperationException("The bridge didn't answer the pairing request.");
            Match pin = Regex.Match(first, "\"Pin\"\\s*:\\s*\"?(\\d{4,})\"?");
            if (!pin.Success) throw new InvalidOperationException("The bridge couldn't start pairing: " + first);

            Request("POST", sunshineUrl.TrimEnd('/') + "/api/pin",
                "{\"pin\":\"" + pin.Groups[1].Value + "\",\"name\":\"" + clientName.Replace("\"", "") + "\"}",
                new CookieContainer(), sunshineUser, sunshinePassword);

            string second = reader.ReadLine();
            if (second == null) throw new InvalidOperationException("The bridge didn't report the pairing result.");
            return second;
        }
    }
}
