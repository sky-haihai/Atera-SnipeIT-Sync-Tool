using System.Xml.Linq;

namespace AteraSnipeSync.Tests.Installer;

/// <summary>
/// Locks the release metadata, WiX installation/uninstall safety contract, and packaging-script gates without executing MSI actions.
/// </summary>
public sealed class InstallerContractTests
{
    private const string ExpectedUpgradeCode = "549B4FDF-C466-4CF0-A356-0EC6380C24CD";
    private const string ExpectedProductNamespace = "AD4D8FDE-7A95-4D4E-8A44-988FAE44D807";
    private const string RemoveCondition = "REMOVELOCALDATA=1 AND REMOVE=\"ALL\" AND NOT UPGRADINGPRODUCTCODE";

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void BuildMetadata_IsFixedForVersionOneRelease()
    {
        var document = LoadXml("Directory.Build.props");

        Assert.Equal("1.0.0", ElementValue(document, "VersionPrefix"));
        Assert.Equal("1.0.0.0", ElementValue(document, "AssemblyVersion"));
        Assert.Equal("1.0.0.0", ElementValue(document, "FileVersion"));
        Assert.Equal("Vue IT Inc.", ElementValue(document, "Company"));
        Assert.Equal("Atera Snipe-IT Auto Sync", ElementValue(document, "Product"));
    }

    [Fact]
    public void PackageIdentity_IsPerMachineX64AndUsesFixedPublisher()
    {
        var package = SingleElement(LoadXml("installer", "AteraSnipeSync.Installer", "Package.wxs"), "Package");
        var project = LoadXml("installer", "AteraSnipeSync.Installer", "AteraSnipeSync.Installer.wixproj");

        Assert.Equal("wix7", ElementValue(project, "AcceptEula"));
        Assert.Equal("Vue IT Inc.", Attribute(package, "Manufacturer"));
        Assert.Equal("$(InstallerVersion)", Attribute(package, "Version"));
        Assert.Equal("$(InstallerProductCode)", Attribute(package, "ProductCode"));
        Assert.Equal(ExpectedUpgradeCode, Attribute(package, "UpgradeCode"));
        Assert.Equal("perMachine", Attribute(package, "Scope"));
        Assert.Equal("x64", Attribute(package, "Platform"));
    }

    [Fact]
    public void WorkerService_InstallsAsAutomaticLocalSystemService()
    {
        var document = LoadXml("installer", "AteraSnipeSync.Installer", "Package.wxs");
        var serviceInstall = SingleElement(document, "ServiceInstall");
        var serviceControl = SingleElement(document, "ServiceControl");

        Assert.Equal("AteraSnipeItAutoSync", Attribute(serviceInstall, "Name"));
        Assert.Equal("LocalSystem", Attribute(serviceInstall, "Account"));
        Assert.Equal("ownProcess", Attribute(serviceInstall, "Type"));
        Assert.Equal("auto", Attribute(serviceInstall, "Start"));
        Assert.Equal("AteraSnipeItAutoSync", Attribute(serviceControl, "Name"));
        Assert.Equal("install", Attribute(serviceControl, "Start"));
        Assert.Equal("both", Attribute(serviceControl, "Stop"));
        Assert.Equal("uninstall", Attribute(serviceControl, "Remove"));
    }

    [Fact]
    public void TrayAndProgramData_AreMachineWideAndUseRequiredPermissions()
    {
        var document = LoadXml("installer", "AteraSnipeSync.Installer", "Package.wxs");
        var runValue = document.Descendants()
            .Single(element => element.Name.LocalName == "RegistryValue" && Attribute(element, "Name") == "AteraSnipeSync.TrayApp");
        var shortcut = SingleElement(document, "Shortcut");
        var permissions = document.Descendants().Where(element => element.Name.LocalName == "PermissionEx").ToArray();

        Assert.Equal("HKLM", Attribute(runValue, "Root"));
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", Attribute(runValue, "Key"));
        Assert.Equal("ApplicationStartMenuFolder", Attribute(shortcut, "Directory"));
        Assert.Contains(permissions, permission => Attribute(permission, "User") == "SYSTEM" && Attribute(permission, "GenericAll") == "yes");
        Assert.Contains(permissions, permission => Attribute(permission, "User") == "Administrators" && Attribute(permission, "GenericAll") == "yes");
        Assert.Contains(permissions, permission =>
            Attribute(permission, "User") == "Users" &&
            Attribute(permission, "CreateChild") == "yes" &&
            Attribute(permission, "Delete") == "yes");
        Assert.Equal(3, document.Descendants().Count(element =>
            element.Name.LocalName == "Component" &&
            new[] { "LogsFolderComponent", "HistoryFolderComponent", "PreflightFolderComponent" }.Contains(Attribute(element, "Id"))));
    }

    [Fact]
    public void RemoveLocalData_IsSecureOptInAndUpgradeSafe()
    {
        var document = LoadXml("installer", "AteraSnipeSync.Installer", "Package.wxs");
        var property = document.Descendants()
            .Single(element => element.Name.LocalName == "Property" && Attribute(element, "Id") == "REMOVELOCALDATA");
        var removal = SingleElement(document, "RemoveFolderEx");
        var search = SingleElement(document, "RegistrySearch");

        Assert.Equal("yes", Attribute(property, "Secure"));
        Assert.Null(property.Attribute("Value"));
        Assert.Equal("ATERASNIPESYNC_PROGRAMDATA_ROOT", Attribute(removal, "Property"));
        Assert.Equal("uninstall", Attribute(removal, "On"));
        Assert.Equal(RemoveCondition, Attribute(removal, "Condition"));
        Assert.Equal("RememberedProgramDataRootSearch", Attribute(search, "Id"));
        Assert.Equal("always64", Attribute(search, "Bitness"));
    }

    [Fact]
    public void RemoveDialog_DefaultsToPreserveAndOnlyRunsForRealUninstall()
    {
        var document = LoadXml("installer", "AteraSnipeSync.Installer", "RemoveLocalDataDlg.wxs");
        var checkBox = document.Descendants()
            .Single(element => element.Name.LocalName == "Control" && Attribute(element, "Id") == "RemoveLocalDataCheckBox");
        var show = SingleElement(document, "Show");
        var text = string.Join(" ", document.Descendants()
            .Where(element => element.Name.LocalName == "Control")
            .Select(element => Attribute(element, "Text")));

        Assert.Equal("REMOVELOCALDATA", Attribute(checkBox, "Property"));
        Assert.Equal("1", Attribute(checkBox, "CheckBoxValue"));
        Assert.Null(checkBox.Attribute("Value"));
        Assert.Equal("Installed AND REMOVE=\"ALL\" AND NOT UPGRADINGPRODUCTCODE", Attribute(show, "Condition"));
        Assert.Contains(@"%ProgramData%\AteraSnipeSync", text, StringComparison.Ordinal);
        Assert.Contains("credentials", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logs", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseScript_RequiresCleanTreeAndSelfContainedPublish()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "Build-Release.ps1"));

        Assert.Contains("[switch]$AllowDirty", script, StringComparison.Ordinal);
        Assert.Contains("status --porcelain=v1", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained", script, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=false", script, StringComparison.Ordinal);
        Assert.Contains("-p:PublishTrimmed=false", script, StringComparison.Ordinal);
        Assert.Contains("Publish merge collision has different content", script, StringComparison.Ordinal);
        Assert.Contains(ExpectedProductNamespace, script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("AteraSnipeSync-$Version-win-x64.msi", script, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerSource_ExcludesDevelopmentAndSensitiveLocalFiles()
    {
        var package = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "installer",
            "AteraSnipeSync.Installer",
            "Package.wxs"));
        var workerProject = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "AteraSnipeSync.WorkerService",
            "AteraSnipeSync.WorkerService.csproj"));
        var trayProject = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "AteraSnipeSync.TrayApp",
            "AteraSnipeSync.TrayApp.csproj"));

        Assert.Contains(@"**\*.pdb", package, StringComparison.Ordinal);
        Assert.Contains("appsettings.Development.json", package, StringComparison.Ordinal);
        Assert.Contains("appsettings.local.json", package, StringComparison.Ordinal);
        Assert.Contains("CopyToPublishDirectory=\"Never\"", workerProject, StringComparison.Ordinal);
        Assert.Contains("<UseWindowsForms>true</UseWindowsForms>", workerProject, StringComparison.Ordinal);
        Assert.Contains("<RuntimeFrameworkVersion>10.0.10</RuntimeFrameworkVersion>", workerProject, StringComparison.Ordinal);
        Assert.Contains("<RuntimeFrameworkVersion>10.0.10</RuntimeFrameworkVersion>", trayProject, StringComparison.Ordinal);
    }

    private static XDocument LoadXml(params string[] relativeSegments)
        => XDocument.Load(Path.Combine(new[] { RepositoryRoot }.Concat(relativeSegments).ToArray()));

    private static XElement SingleElement(XDocument document, string localName)
        => document.Descendants().Single(element => element.Name.LocalName == localName);

    private static string ElementValue(XDocument document, string localName)
        => SingleElement(document, localName).Value;

    private static string Attribute(XElement element, string localName)
        => element.Attribute(localName)?.Value ?? string.Empty;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AteraSnipeSync.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing AteraSnipeSync.sln.");
    }
}
