// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECOMPUTE003

using System.ComponentModel;
using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Logging;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Provides factory methods for creating common container registry login callbacks.
/// </summary>
/// <remarks>
/// <para>
/// These helpers use CLI-based authentication which requires the respective CLI tools
/// (Azure CLI, AWS CLI, Docker) to be installed and configured. For Azure Container Registry,
/// the user must be logged in via <c>az login</c>. For AWS ECR, valid credentials must be configured.
/// </para>
/// </remarks>
public static class PulumiContainerRegistryHelpers
{
    /// <summary>
    /// Creates a <see cref="ProcessStartInfo"/> for running a CLI command.
    /// </summary>
    private static ProcessStartInfo CreateCliProcessStartInfo(string command, string arguments, bool isNativeExecutable)
    {
        // On Windows, batch scripts (.cmd files) need cmd.exe to execute them
        // when UseShellExecute = false (required for stdout/stderr redirection).
        var needsShellWrapper = OperatingSystem.IsWindows() && !isNativeExecutable;

        return new ProcessStartInfo
        {
            FileName = needsShellWrapper ? "cmd.exe" : command,
            Arguments = needsShellWrapper ? $"/c {command} {arguments}" : arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    /// <summary>
    /// Creates a login callback for Azure Container Registry using Azure CLI.
    /// </summary>
    /// <returns>A callback that runs <c>az acr login --name {registryName}</c>.</returns>
    /// <remarks>
    /// This callback requires the Azure CLI to be installed and the user to be authenticated via <c>az login</c>.
    /// </remarks>
    public static Func<PipelineStepContext, IContainerRegistry, Task> CreateAzureCliLoginCallback()
    {
        return async (context, registry) =>
        {
            var registryName = await registry.Name.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(registryName))
            {
                throw new InvalidOperationException(
                    "Registry name not available. Ensure the registry is provisioned and the 'name' output is defined.");
            }

            context.Logger.LogInformation("Logging in to Azure Container Registry '{RegistryName}'", registryName);

            await context.ReportingStep.CompleteAsync(
                $"Logging in to ACR '{registryName}'",
                CompletionState.InProgress,
                context.CancellationToken).ConfigureAwait(false);

            var processStartInfo = CreateCliProcessStartInfo("az", $"acr login --name {registryName}", isNativeExecutable: false);

            using var process = new Process { StartInfo = processStartInfo };

            try
            {
                process.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2) // ERROR_FILE_NOT_FOUND
            {
                throw new InvalidOperationException(
                    "Azure CLI ('az') is not installed or not found in PATH. " +
                    "Please install the Azure CLI from https://aka.ms/installazurecli and ensure you are logged in with 'az login'.",
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
                await context.ReportingStep.CompleteAsync(
                    $"Azure ACR login failed",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException($"Azure ACR login failed with exit code {process.ExitCode}. Error: {error}");
            }

            await context.ReportingStep.CompleteAsync(
                $"Logged in to ACR '{registryName}'",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Creates a login callback for AWS Elastic Container Registry using AWS CLI.
    /// </summary>
    /// <param name="region">Optional AWS region. If not specified, uses the default region from AWS configuration.</param>
    /// <returns>A callback that runs <c>aws ecr get-login-password | docker login</c>.</returns>
    public static Func<PipelineStepContext, IContainerRegistry, Task> CreateAwsEcrLoginCallback(string? region = null)
    {
        return async (context, registry) =>
        {
            var registryEndpoint = await registry.Endpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(registryEndpoint))
            {
                throw new InvalidOperationException(
                    "Registry endpoint not available. Ensure the registry is provisioned.");
            }

            context.Logger.LogInformation("Logging in to AWS ECR '{RegistryEndpoint}'", registryEndpoint);

            await context.ReportingStep.CompleteAsync(
                $"Logging in to ECR '{registryEndpoint}'",
                CompletionState.InProgress,
                context.CancellationToken).ConfigureAwait(false);

            // Build the AWS ECR get-login-password command
            var regionArg = string.IsNullOrEmpty(region) ? "" : $" --region {region}";
            var awsCommand = $"ecr get-login-password{regionArg}";

            // Get the password from AWS CLI
            var awsProcessStartInfo = CreateCliProcessStartInfo("aws", awsCommand, isNativeExecutable: false);

            using var awsProcess = new Process { StartInfo = awsProcessStartInfo };

            try
            {
                awsProcess.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                throw new InvalidOperationException(
                    "AWS CLI ('aws') is not installed or not found in PATH. " +
                    "Please install the AWS CLI from https://aws.amazon.com/cli/ and configure your credentials.",
                    ex);
            }

            var passwordTask = awsProcess.StandardOutput.ReadToEndAsync(context.CancellationToken);
            var awsErrorTask = awsProcess.StandardError.ReadToEndAsync(context.CancellationToken);

            await awsProcess.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

            var password = (await passwordTask.ConfigureAwait(false)).Trim();
            var awsError = await awsErrorTask.ConfigureAwait(false);

            if (awsProcess.ExitCode != 0)
            {
                await context.ReportingStep.CompleteAsync(
                    "AWS ECR get-login-password failed",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException($"AWS ECR get-login-password failed with exit code {awsProcess.ExitCode}. Error: {awsError}");
            }

            // Docker login with the password via stdin
            var dockerProcessStartInfo = CreateCliProcessStartInfo("docker", $"login --username AWS --password-stdin {registryEndpoint}", isNativeExecutable: true);
            dockerProcessStartInfo.RedirectStandardInput = true;

            using var dockerProcess = new Process { StartInfo = dockerProcessStartInfo };

            try
            {
                dockerProcess.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                throw new InvalidOperationException(
                    "Docker is not installed or not found in PATH.",
                    ex);
            }

            await dockerProcess.StandardInput.WriteAsync(password).ConfigureAwait(false);
            dockerProcess.StandardInput.Close();

            var dockerOutputTask = dockerProcess.StandardOutput.ReadToEndAsync(context.CancellationToken);
            var dockerErrorTask = dockerProcess.StandardError.ReadToEndAsync(context.CancellationToken);

            await dockerProcess.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

            var dockerOutput = await dockerOutputTask.ConfigureAwait(false);
            var dockerError = await dockerErrorTask.ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(dockerOutput))
            {
                context.Logger.LogDebug("Docker login output: {Output}", dockerOutput);
            }

            if (dockerProcess.ExitCode != 0)
            {
                await context.ReportingStep.CompleteAsync(
                    "Docker login to ECR failed",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException($"Docker login to ECR failed with exit code {dockerProcess.ExitCode}. Error: {dockerError}");
            }

            await context.ReportingStep.CompleteAsync(
                $"Logged in to ECR '{registryEndpoint}'",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Creates a generic Docker login callback using username and password.
    /// </summary>
    /// <param name="username">The registry username.</param>
    /// <param name="password">The registry password (will be passed via stdin).</param>
    /// <returns>A callback that runs <c>docker login --password-stdin {endpoint}</c>.</returns>
    public static Func<PipelineStepContext, IContainerRegistry, Task> CreateDockerLoginCallback(
        string username,
        string password)
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

            context.Logger.LogInformation("Logging in to Docker registry '{RegistryEndpoint}'", registryEndpoint);

            await context.ReportingStep.CompleteAsync(
                $"Logging in to registry '{registryEndpoint}'",
                CompletionState.InProgress,
                context.CancellationToken).ConfigureAwait(false);

            var processStartInfo = CreateCliProcessStartInfo("docker", $"login --username {username} --password-stdin {registryEndpoint}", isNativeExecutable: true);
            processStartInfo.RedirectStandardInput = true;

            using var process = new Process { StartInfo = processStartInfo };

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

            var outputTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(context.CancellationToken);

            await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(output))
            {
                context.Logger.LogDebug("Docker login output: {Output}", output);
            }

            if (process.ExitCode != 0)
            {
                await context.ReportingStep.CompleteAsync(
                    "Docker login failed",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException($"Docker login failed with exit code {process.ExitCode}. Error: {error}");
            }

            await context.ReportingStep.CompleteAsync(
                $"Logged in to registry '{registryEndpoint}'",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
        };
    }
}
