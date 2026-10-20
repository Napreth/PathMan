[Console]::OutputEncoding = [Text.Encoding]::UTF8
[ordered]@{
    Arguments = @($args)
    WorkingDirectory = [Environment]::CurrentDirectory
    Environment = $env:PATHMAN_TEST_SENTINEL
    StandardInput = [Console]::In.ReadToEnd()
} | ConvertTo-Json -Compress
exit 37
