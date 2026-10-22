namespace Krissvia.Core.Tests;

/// <summary>La version affichée : ce que le SDK .NET écrit dans le binaire, rendu lisible.</summary>
public class BuildVersionTests
{
    [Fact]
    public void Le_commit_ecrit_par_le_sdk_est_lu_et_raccourci_comme_dans_git_log()
    {
        var v = BuildVersion.Parse("1.0.0+edc186d3e1e4b68ea2d706689120d2ffbfec34db");

        Assert.Equal("1.0.0", v.Version);
        Assert.Equal("edc186d3e1e4b68ea2d706689120d2ffbfec34db", v.Commit);
        Assert.Equal("edc186d", v.ShortCommit);
        Assert.Equal("1.0.0 · edc186d", v.Label);
    }

    [Fact]
    public void Sans_commit_la_version_seule_et_sans_rien_un_point_d_interrogation()
    {
        Assert.Equal(new BuildVersion("1.0.0", null), BuildVersion.Parse("1.0.0"));
        Assert.Equal("1.0.0", BuildVersion.Parse("1.0.0+").Label);
        Assert.Equal("?", BuildVersion.Parse(null).Label);
    }

    [Fact]
    public void Le_binaire_des_tests_porte_lui_aussi_sa_version()
    {
        Assert.NotEqual("?", BuildVersion.Of(typeof(BuildVersion).Assembly).Version);
    }
}
