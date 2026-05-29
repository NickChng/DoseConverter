$p = '\\spvaimapcn\data$\Apps\DoseConverter\DoseConverter.csproj'
$t = Get-Content -Raw $p

$old = '  <ItemGroup />'
$new = @'
  <ItemGroup />
  <ItemGroup>
	<Content Include="packages\SachaBr.SimpleITK.Runtime.2.5.5\runtimes\win-x64\native\SimpleITKCSharpNative.dll">
	  <CopyToOutputDirectory>Always</CopyToOutputDirectory>
	  <Link>SimpleITKCSharpNative.dll</Link>
	</Content>
  </ItemGroup>
'@

if ($t.Contains($old)) {
	$t = $t.Replace($old, $new)
	Set-Content -Path $p -Value $t -NoNewline
	Write-Host 'OK'
} else {
	Write-Host 'MARKER NOT FOUND'
}
