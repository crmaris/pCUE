namespace pCUE;
// Linux uses its package manager; only the shared API's version field needs this bridge.
internal static class AppUpdateService { public static string InstalledVersion => Pcue.Linux.Program.Version; }
