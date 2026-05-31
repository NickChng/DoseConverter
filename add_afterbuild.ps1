$path = "\\spvaimapcn\data$\Apps\DoseConverter\DoseConverter.csproj"
$lines = [System.Collections.Generic.List[string]](Get-Content $path)

# Find the </Project> closing line and insert the AfterBuild target before it
$insertIdx = -1
for ($i = $lines.Count - 1; $i -ge 0; $i--) {
	if ($lines[$i] -match '^\s*</Project>') {
		$insertIdx = $i
		break
	}
}

if ($insertIdx -lt 0) {
	Write-Host "ERROR: Could not find </Project>"
	exit 1
}

$newBlock = @(
	'  <Target Name="CopySimpleITKManaged" AfterTargets="Build">',
	'    <Copy SourceFiles="$(MSBuildProjectDirectory)\packages\SimpleITKCSharpManaged.2.5.3.2\lib\netstandard2.0\SimpleITKCSharpManaged.dll"',
	'          DestinationFolder="$(OutputPath)"',
	'          SkipUnchangedFiles="true" />',
	'  </Target>'
)

for ($i = 0; $i -lt $newBlock.Count; $i++) {
	$lines.Insert($insertIdx + $i, $newBlock[$i])
}

[System.IO.File]::WriteAllLines($path, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Done. AfterBuild target inserted at line $($insertIdx + 1). Total lines: $($lines.Count)"
