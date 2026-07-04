using Raizen.Endpoint.Service.Actions;

namespace Raizen.Tests;

public sealed class RunAsAdminHandlerTests
{
    [Fact]
    public void BuildLaunchTarget_Exe_LaunchesDirectly()
    {
        var target = RunAsAdminHandler.BuildLaunchTarget(@"C:\Tools\AdminTool.exe");

        Assert.Equal(@"C:\Tools\AdminTool.exe", target.ApplicationPath);
        Assert.Null(target.CommandLine);
        Assert.Equal(@"C:\Tools", target.WorkingDirectory);
    }

    [Fact]
    public void BuildLaunchTarget_Msi_UsesMsiexecInstall()
    {
        var target = RunAsAdminHandler.BuildLaunchTarget(@"C:\Installers\Widget.msi");

        Assert.EndsWith(@"\msiexec.exe", target.ApplicationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/i \"C:\\Installers\\Widget.msi\"", target.CommandLine);
        Assert.Equal(@"C:\Installers", target.WorkingDirectory);
    }

    [Fact]
    public void BuildLaunchTarget_Msc_UsesMmc()
    {
        var target = RunAsAdminHandler.BuildLaunchTarget(@"C:\Tools\console.msc");

        Assert.EndsWith(@"\mmc.exe", target.ApplicationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"C:\\Tools\\console.msc\"", target.CommandLine);
        Assert.Equal(@"C:\Tools", target.WorkingDirectory);
    }
}
