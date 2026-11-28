# Recompile the Vulkan traced-view shader (src/RefractorForge.VkTrace/Shaders/trace.comp -> trace.spv).
# Needs the Vulkan SDK's glslangValidator; the .spv is checked in, so an ordinary build does not.
param([string]$Sdk = $env:VULKAN_SDK)
if (-not $Sdk) { $Sdk = 'C:\VulkanSDK\1.3.296.0' }
$dir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\RefractorForge.VkTrace\Shaders'
& (Join-Path $Sdk 'Bin\glslangValidator.exe') -V --target-env vulkan1.2 (Join-Path $dir 'trace.comp') -o (Join-Path $dir 'trace.spv')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $Sdk 'Bin\spirv-val.exe') --target-env vulkan1.2 (Join-Path $dir 'trace.spv')
exit $LASTEXITCODE
