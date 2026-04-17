namespace DSoft.VersionChanger.Enums
{
    /// <summary>
    /// SemVer 2.0 pre-release preset. Drives the suffix applied to the version
    /// (e.g. "alpha", "beta.2", "rc.1"). <see cref="Final"/> means no suffix.
    /// </summary>
    public enum PreReleaseType
    {
        Final,
        Alpha,
        Beta,
        RC,
        Custom,
    }
}
