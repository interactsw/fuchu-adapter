param
(
    [Parameter(Mandatory=$True, ParameterSetName='Preview')]
    [switch]$Preview,
    [Parameter(Mandatory=$True, ParameterSetName='Release')]
    [switch]$Release
)
$VersionInfo = GitVersion | ConvertFrom-Json
if ($Preview)
{
    $TagBase = $VersionInfo.FullSemVer
}
else
{
    $TagBase = $VersionInfo.MajorMinorPatch
}
$Tag = "v" + $TagBase
git tag $Tag
git push origin $Tag