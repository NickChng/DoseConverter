$path = "\\spvaimapcn\data$\Apps\DoseConverter\DoseConverter.csproj"
$lines = [System.Collections.Generic.List[string]](Get-Content $path)
$out = [System.Collections.Generic.List[string]]::new()
$mDone = $false
$cDone = $false
for ($i = 0; $i -lt $lines.Count; $i++) {
	$l = $lines[$i]
	$out.Add($l)
	if (!$mDone -and $l -match 'HintPath.*SimpleITKCSharpManaged') {
		$out.Add('      <Private>true</Private>')
		$mDone = $true
	}
	if (!$cDone -and $l -match '</Content>' -and $i -ge 3 -and $lines[$i-3] -match 'SimpleITKCSharpNative') {
		$out.Add('    <Content Include="packages\SimpleITKCSharpManaged.2.5.3.2\lib\netstandard2.0\SimpleITKCSharpManaged.dll">')
		$out.Add('      <CopyToOutputDirectory>Always</CopyToOutputDirectory>')
		$out.Add('      <Link>SimpleITKCSharpManaged.dll</Link>')
		$out.Add('    </Content>')
		$cDone = $true
	}
}
[System.IO.File]::WriteAllLines($path, $out, [System.Text.UTF8Encoding]::new($false))
Write-Host "Done. private=$mDone content=$cDone total=$($out.Count)"
