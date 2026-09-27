# PSScriptAnalyzer settings for the PowerShell scripts under tools/.
# Scripts must run on Windows PowerShell 5.1 as well as PowerShell 7.x.
@{
    Severity     = @('Error', 'Warning')
    IncludeDefaultRules = $true
    Rules        = @{
        PSUseCompatibleSyntax = @{
            Enable         = $true
            TargetVersions = @('5.1', '7.4')
        }
    }
}
