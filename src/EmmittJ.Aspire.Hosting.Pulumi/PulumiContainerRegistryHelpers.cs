// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIRECOMPUTE003  // IContainerRegistry is experimental

using System.ComponentModel;
using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Factory methods for common container registry login callbacks.
/// </summary>
/// <remarks>
/// These callbacks use CLI-based authentication and require the relevant CLI tools to be installed and
/// configured: the Azure CLI (logged in via <c>az login</c>) for Azure Container Registry, the AWS CLI with
/// valid credentials for Elastic Container Registry, and Docker for the generic login.
/// </remarks>
public static class PulumiContainerRegistryHelpers
{
    /// <summary>
    /// Creates a login callback for Azure Container Registry that runs <c>az acr login --name {registryName}</c>.
    /// </summary>
    public static Func<PipelineStepContext, IContainerRegistry, Task> CreateAzureCliLoginCallback()
    {
        return async (context, registry) =>
        {
            var registryName = await registry.Name.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(registryName))
            {
                throw new InvalidOperationException(
                    "Registry name not available. Ensure the registry is provisioned before the login step runs.");
            }

            context.Logger.LogInformation("Logging in to Azure Container Registry '{RegistryName}'.", registryName);

            var startInfo = CreateCliProcessStartInfo("az", $"acr login --name {registryName}", isNativeExecutable: false);
            using var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2) // ERROR_FILE_NOT_FOUND
            {
                throw new InvalidOperationException(
                    "Azure CLI ('az') is not installed or not found in PATH. Install it from https://aka.ms/installazurecli and run 'az login'.",
                    ex);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(context.CancellationToken);
            await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(output))
            {
                context.Logger.LogDebug("Azure CLI output: {Output}", output);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Azure ACR login failed with exit code {process.ExitCode}. Error: {error}");
            }
        };
    }

    /// <summary>
    /// Creates a login callback for AWS Elastic Container Registry that pipes
    /// <c>aws ecr get-login-password</c> into <c>docker login</c>.
    /// </summary>
    /// <param name="region">Optional AWS region. Defaults to the region from the AWS configuration.</param>
    public static Func<PipelineStepContext, IContainerRegistry, Task> CreateAwsEcrLoginCallback(string? region = null)
    {
        return async (context, registry) =>
        {
            var registryEndpoint = await registry.Endpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(registryEndpoint))
            {
                throw new InvalidOperationException("Registry endpoint not available. Ensure the registry is provisioned.");
            }

            context.Logger.LogInformation("Logging in to AWS ECR '{RegistryEndpoint}'.", registryEndpoint);

            var regionArg = string.IsNullOrEmpty(region) ? "" : $" --region {region}";
            var awsStartInfo = CreateCliProcessStartInfo("aws", $"ecr get-login-password{regionArg}", isNativeExecutable: false);
            using var awsProcess = new Process { StartInfo = awsStartInfo };

            try
            {
                awsProcess.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                throw new InvalidOperationException(
                    "AWS CLI ('aws') is not installed or not found in PATH. Install it from https://aws.amazon.com/cli/ and configure your credentials.",
                    ex);
            }

            var passwordTask = awsProcess.StandardOutput.ReadToEndAsync(context.CancellationToken);
            var awsErrorTask = awsProcess.StandardError.ReadToEndAsync(context.CancellationToken);
            await awsProcess.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);
            var password = (await passwordTask.ConfigureAwait(false)).Trim();
            var awsError = await awsErrorTask.ConfigureAwait(false);

            if (awsProcess.ExitCode != 0)
            {
                throw new InvalidOperationException($"AWS ECR get-login-password failed with exit code {awsProcess.ExitCode}. Error: {awsError}");
            }

            var dockerStartInfo = CreateCliProcessStartInfo("docker", $"login --username AWS --password-stdin {registryEndpoint}", isNativeExecutable: true);
            dockerStartInfo.RedirectStandardInput = true;
            using var dockerProcess = new Process { StartInfo = dockerStartInfo };

            try
            {
                dockerProcess.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                throw new InvalidOperationException("Docker is not installed or not found in PATH.", ex);
            }

            await dockerProcess.StandardInput.WriteAsync(password).ConfigureAwait(false);
            dockerProcess.StandardInput.Close();

            var dockerErrorTask = dockerProcess.StandardError.ReadToEndAsync(context.CancellationToken);
            await dockerProcess.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);
            var dockerError = await dockerErrorTask.ConfigureAwait(false);

            if (dockerProcess.ExitCode != 0)
            {
                throw new InvalidOperationException($"Docker login to ECR failed with exit code {dockerProcess.ExitCode}. Error: {dockerError}");
            }
        };
    }

    /// <summary>
    /// Creates a generic Docker login callback that runs <c>docker login --username {username} --password-stdin {endpoint}</c>.
    /// </summary>
    /// <param name="username">The registry username.</param>
    /// <param name="password">The registry password, passed to Docker via stdin.</param>
    public static Func<PipelineStepContext, IContainerRegistry, Task> CreateDockerLoginCallback(string username, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        return async (context, registry) =>
        {
            var registryEndpoint = await registry.Endpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(registryEndpoint))
            {
                throw new InvalidOperationException("Registry endpoint not available.");
            }

            context.Logger.LogInformation("Logging in to Docker registry '{RegistryEndpoint}'.", registryEndpoint);

            var startInfo = CreateCliProcessStartInfo("docker", $"login --username {username} --password-stdin {registryEndpoint}", isNativeExecutable: true);
            startInfo.RedirectStandardInput = true;
            using var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                throw new InvalidOperationException("Docker is not installed or not found in PATH.", ex);
            }

            await process.StandardInput.WriteAsync(password).ConfigureAwait(false);
            process.StandardInput.Close();

            var errorTask = process.StandardError.ReadToEndAsync(context.CancellationToken);
            await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Docker login failed with exit code {process.ExitCode}. Error: {error}");
            }
        };
    }

    // On Windows, batch scripts (.cmd shims like az/aws) need cmd.exe to execute them when UseShellExecute is
    // false (which is required for stdout/stderr redirection). Native executables (docker) run directly.
    private static ProcessStartInfo CreateCliProcessStartInfo(string command, string arguments, bool isNativeExecutable)
    {
        var needsShellWrapper = OperatingSystem.IsWindows() && !isNativeExecutable;

        return new ProcessStartInfo
        {
            FileName = needsShellWrapper ? "cmd.exe" : command,
            Arguments = needsShellWrapper ? $"/c {command} {arguments}" : arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }
}
