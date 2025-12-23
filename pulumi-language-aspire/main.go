// Copyright 2024, EmmittJ. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// pulumi-language-aspire is the Pulumi language host for Aspire applications.
// It delegates to the Aspire CLI for deployment operations.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/pkg/errors"
	"github.com/pulumi/pulumi/sdk/v3/go/common/util/cmdutil"
	"github.com/pulumi/pulumi/sdk/v3/go/common/util/logging"
	"github.com/pulumi/pulumi/sdk/v3/go/common/util/rpcutil"
	pulumirpc "github.com/pulumi/pulumi/sdk/v3/proto/go"
	"google.golang.org/grpc"
	"google.golang.org/protobuf/types/known/emptypb"
)

// Version is set at build time.
var Version string

func main() {
	var tracing string
	var root string
	flag.StringVar(&tracing, "tracing", "", "Emit tracing to a Zipkin-compatible tracing endpoint")
	flag.StringVar(&root, "root", "", "Root directory for the Aspire project")
	flag.Parse()

	args := flag.Args()
	logging.InitLogging(false, 0, false)
	cmdutil.InitTracing("pulumi-language-aspire", "pulumi-language-aspire", tracing)

	var engineAddress string
	if len(args) > 0 {
		engineAddress = args[0]
	}

	// Set up signal handling for graceful shutdown
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	// Map the context Done channel to the rpcutil boolean cancel channel.
	// The context will close on SIGINT or Healthcheck failure.
	cancelChannel := make(chan bool)
	go func() {
		<-ctx.Done()
		cancel() // remove the interrupt handler
		close(cancelChannel)
	}()

	// Start healthcheck to monitor engine connection
	err := rpcutil.Healthcheck(ctx, engineAddress, 5*time.Minute, cancel)
	if err != nil {
		cmdutil.Exit(errors.Wrapf(err, "could not start health check host RPC server"))
	}

	// Create the language host
	host := &aspireLanguageHost{
		engineAddress: engineAddress,
		tracing:       tracing,
	}

	// Start the gRPC server using ServeWithOptions for proper lifecycle management
	handle, err := rpcutil.ServeWithOptions(rpcutil.ServeOptions{
		Cancel: cancelChannel,
		Init: func(srv *grpc.Server) error {
			pulumirpc.RegisterLanguageRuntimeServer(srv, host)
			return nil
		},
		Options: rpcutil.OpenTracingServerInterceptorOptions(nil),
	})
	if err != nil {
		cmdutil.Exit(fmt.Errorf("could not start language host RPC server: %w", err))
	}

	// Print the port for the engine to connect
	fmt.Printf("%d\n", handle.Port)

	// Wait for the server to stop
	if err := <-handle.Done; err != nil {
		cmdutil.Exit(fmt.Errorf("language host RPC stopped serving: %w", err))
	}
}

// aspireLanguageHost implements the Pulumi language runtime for Aspire.
type aspireLanguageHost struct {
	pulumirpc.UnimplementedLanguageRuntimeServer
	engineAddress string
	tracing       string
}

// Handshake is the first call made by the engine to establish connection.
func (host *aspireLanguageHost) Handshake(
	ctx context.Context,
	req *pulumirpc.LanguageHandshakeRequest,
) (*pulumirpc.LanguageHandshakeResponse, error) {
	logging.V(5).Infof("Handshake: engine=%s", req.EngineAddress)

	// Store the engine address from handshake
	if req.EngineAddress != "" {
		host.engineAddress = req.EngineAddress
	}

	return &pulumirpc.LanguageHandshakeResponse{}, nil
}

// GetRequiredPlugins returns the plugins required by the Aspire project.
func (host *aspireLanguageHost) GetRequiredPlugins(
	ctx context.Context,
	req *pulumirpc.GetRequiredPluginsRequest,
) (*pulumirpc.GetRequiredPluginsResponse, error) {
	logging.V(5).Infof("GetRequiredPlugins: %s", req.Program)

	// Parse the project to find Pulumi provider packages
	plugins, err := host.getPluginsFromProject(req.Program)
	if err != nil {
		return nil, fmt.Errorf("failed to get plugins: %w", err)
	}

	return &pulumirpc.GetRequiredPluginsResponse{Plugins: plugins}, nil
}

// Run executes the Aspire program.
func (host *aspireLanguageHost) Run(
	ctx context.Context,
	req *pulumirpc.RunRequest,
) (*pulumirpc.RunResponse, error) {
	logging.V(5).Infof("Run: program=%s pwd=%s", req.Program, req.Pwd)

	// Find the Aspire project directory
	projectDir := req.Pwd
	if req.Info != nil && req.Info.ProgramDirectory != "" {
		projectDir = req.Info.ProgramDirectory
	}

	// Serialize config for passing to aspire
	config, err := host.constructConfig(req)
	if err != nil {
		return nil, errors.Wrap(err, "failed to serialize configuration")
	}
	configSecretKeys, err := host.constructConfigSecretKeys(req)
	if err != nil {
		return nil, errors.Wrap(err, "failed to serialize configuration secret keys")
	}

	// Run `aspire deploy --non-interactive` which will invoke our SDK
	cmd := exec.CommandContext(ctx, "aspire", "deploy", "--non-interactive")
	cmd.Dir = projectDir

	// Construct environment variables like dotnet language host
	cmd.Env = host.constructEnv(req, config, configSecretKeys)

	// Wire up stdout/stderr directly
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if logging.V(5) {
		logging.V(5).Infof("Language host launching: aspire deploy --non-interactive in %s", projectDir)
	}

	// Start the command
	if err := cmd.Start(); err != nil {
		return nil, errors.Wrap(err, "failed to start aspire deploy")
	}

	// Wait for the command to complete
	var errResult string
	if err := cmd.Wait(); err != nil {
		if exiterr, ok := err.(*exec.ExitError); ok {
			// If the program ran, but exited with a non-zero error code
			if status, stok := exiterr.Sys().(syscall.WaitStatus); stok {
				exitCode := status.ExitStatus()
				// Exit code 6 is a known Windows console cleanup issue
				if exitCode == 6 {
					logging.V(3).Infof("Ignoring exit code %d (Windows console cleanup issue)", exitCode)
					return &pulumirpc.RunResponse{}, nil
				}
				errResult = fmt.Sprintf("aspire deploy exited with code: %d", exitCode)
			} else {
				errResult = fmt.Sprintf("aspire deploy exited unexpectedly: %v", exiterr)
			}
		} else {
			// Couldn't even run the command
			errResult = fmt.Sprintf("failed to execute aspire deploy: %v", err)
		}
	}

	return &pulumirpc.RunResponse{Error: errResult}, nil
}

// constructEnv builds the environment variables for the aspire deploy command.
func (host *aspireLanguageHost) constructEnv(req *pulumirpc.RunRequest, config, configSecretKeys string) []string {
	env := os.Environ()

	maybeAppendEnv := func(k, v string) {
		if v != "" {
			env = append(env, strings.ToUpper("PULUMI_"+k)+"="+v)
		}
	}

	maybeAppendEnv("monitor", req.GetMonitorAddress())
	maybeAppendEnv("engine", host.engineAddress)
	maybeAppendEnv("organization", req.GetOrganization())
	maybeAppendEnv("project", req.GetProject())
	if req.GetInfo() != nil {
		maybeAppendEnv("root_directory", req.GetInfo().RootDirectory)
	}
	maybeAppendEnv("stack", req.GetStack())
	maybeAppendEnv("pwd", req.GetPwd())
	maybeAppendEnv("dry_run", strconv.FormatBool(req.GetDryRun()))
	maybeAppendEnv("query_mode", strconv.FormatBool(req.GetQueryMode()))
	maybeAppendEnv("parallel", strconv.Itoa(int(req.GetParallel())))
	maybeAppendEnv("tracing", host.tracing)
	maybeAppendEnv("config", config)
	maybeAppendEnv("config_secret_keys", configSecretKeys)

	// Also set as PULUMI_RUNTIME for backwards compatibility
	env = append(env, "PULUMI_RUNTIME=aspire")

	return env
}

// constructConfig json-serializes the configuration data given as part of a RunRequest.
func (host *aspireLanguageHost) constructConfig(req *pulumirpc.RunRequest) (string, error) {
	configMap := req.GetConfig()
	if configMap == nil {
		return "", nil
	}

	configJSON, err := json.Marshal(configMap)
	if err != nil {
		return "", err
	}

	return string(configJSON), nil
}

// constructConfigSecretKeys JSON-serializes the list of keys that contain secret values.
func (host *aspireLanguageHost) constructConfigSecretKeys(req *pulumirpc.RunRequest) (string, error) {
	configSecretKeys := req.GetConfigSecretKeys()
	if configSecretKeys == nil {
		return "[]", nil
	}

	configSecretKeysJSON, err := json.Marshal(configSecretKeys)
	if err != nil {
		return "", err
	}

	return string(configSecretKeysJSON), nil
}

// GetPluginInfo returns information about this language plugin.
func (host *aspireLanguageHost) GetPluginInfo(
	ctx context.Context,
	req *emptypb.Empty,
) (*pulumirpc.PluginInfo, error) {
	v := Version
	if v == "" {
		v = "0.0.1"
	}
	return &pulumirpc.PluginInfo{Version: v}, nil
}

// InstallDependencies installs dependencies for the Aspire project.
func (host *aspireLanguageHost) InstallDependencies(
	req *pulumirpc.InstallDependenciesRequest,
	server pulumirpc.LanguageRuntime_InstallDependenciesServer,
) error {
	logging.V(5).Infof("InstallDependencies: %s", req.Directory)

	ctx := server.Context()

	// Run dotnet restore
	cmd := exec.CommandContext(ctx, "dotnet", "restore")
	cmd.Dir = req.Directory
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		return fmt.Errorf("dotnet restore failed: %w", err)
	}

	return nil
}

// About returns information about the runtime.
func (host *aspireLanguageHost) About(
	ctx context.Context,
	req *pulumirpc.AboutRequest,
) (*pulumirpc.AboutResponse, error) {
	// Get dotnet version
	cmd := exec.CommandContext(ctx, "dotnet", "--version")
	output, err := cmd.Output()
	dotnetVersion := "unknown"
	if err == nil {
		dotnetVersion = strings.TrimSpace(string(output))
	}

	// Get aspire version
	cmd = exec.CommandContext(ctx, "aspire", "--version")
	output, err = cmd.Output()
	aspireVersion := "unknown"
	if err == nil {
		aspireVersion = strings.TrimSpace(string(output))
	}

	return &pulumirpc.AboutResponse{
		Executable: "aspire",
		Version:    aspireVersion,
		Metadata: map[string]string{
			"dotnet-version": dotnetVersion,
			"aspire-version": aspireVersion,
		},
	}, nil
}

// GetProgramDependencies returns the dependencies of the program.
func (host *aspireLanguageHost) GetProgramDependencies(
	ctx context.Context,
	req *pulumirpc.GetProgramDependenciesRequest,
) (*pulumirpc.GetProgramDependenciesResponse, error) {
	logging.V(5).Infof("GetProgramDependencies: %s", req.Program)

	// Parse .csproj files to find dependencies
	deps, err := host.getDependencies(req.Program)
	if err != nil {
		return nil, fmt.Errorf("failed to get dependencies: %w", err)
	}

	return &pulumirpc.GetProgramDependenciesResponse{Dependencies: deps}, nil
}

// getPluginsFromProject scans the project for Pulumi provider packages.
func (host *aspireLanguageHost) getPluginsFromProject(programDir string) ([]*pulumirpc.PluginDependency, error) {
	var plugins []*pulumirpc.PluginDependency

	// Look for common Pulumi providers in the project
	// In a full implementation, we'd parse the .csproj files
	knownProviders := map[string]string{
		"Pulumi.AzureNative": "azure-native",
		"Pulumi.Aws":         "aws",
		"Pulumi.Kubernetes":  "kubernetes",
		"Pulumi.Gcp":         "gcp",
	}

	// Check if any of the known providers are referenced
	files, err := filepath.Glob(filepath.Join(programDir, "**", "*.csproj"))
	if err != nil {
		return nil, err
	}

	for _, file := range files {
		content, err := os.ReadFile(file)
		if err != nil {
			continue
		}

		for pkg, name := range knownProviders {
			if strings.Contains(string(content), pkg) {
				plugins = append(plugins, &pulumirpc.PluginDependency{
					Name:    name,
					Kind:    "resource",
					Version: "", // Let Pulumi resolve the version
				})
			}
		}
	}

	return plugins, nil
}

// getDependencies returns the NuGet dependencies of the project.
func (host *aspireLanguageHost) getDependencies(programDir string) ([]*pulumirpc.DependencyInfo, error) {
	var deps []*pulumirpc.DependencyInfo

	// In a full implementation, we'd use `dotnet list package` to get dependencies
	// For now, return an empty list
	return deps, nil
}

// Pack packs the project into a deployable artifact.
func (host *aspireLanguageHost) Pack(
	ctx context.Context,
	req *pulumirpc.PackRequest,
) (*pulumirpc.PackResponse, error) {
	// Aspire projects don't need special packing
	return &pulumirpc.PackResponse{}, nil
}

// GenerateProject generates a new project.
func (host *aspireLanguageHost) GenerateProject(
	ctx context.Context,
	req *pulumirpc.GenerateProjectRequest,
) (*pulumirpc.GenerateProjectResponse, error) {
	// Could integrate with `aspire new` in the future
	return &pulumirpc.GenerateProjectResponse{}, nil
}

// GeneratePackage generates SDK packages.
func (host *aspireLanguageHost) GeneratePackage(
	ctx context.Context,
	req *pulumirpc.GeneratePackageRequest,
) (*pulumirpc.GeneratePackageResponse, error) {
	// Not applicable for Aspire
	return &pulumirpc.GeneratePackageResponse{}, nil
}

// GenerateProgram generates a program from a PCL definition.
func (host *aspireLanguageHost) GenerateProgram(
	ctx context.Context,
	req *pulumirpc.GenerateProgramRequest,
) (*pulumirpc.GenerateProgramResponse, error) {
	// Could generate Aspire AppHost code in the future
	return &pulumirpc.GenerateProgramResponse{}, nil
}

// RuntimeOptionsPrompts returns prompts for runtime options.
func (host *aspireLanguageHost) RuntimeOptionsPrompts(
	ctx context.Context,
	req *pulumirpc.RuntimeOptionsRequest,
) (*pulumirpc.RuntimeOptionsResponse, error) {
	return &pulumirpc.RuntimeOptionsResponse{}, nil
}

// GetRequiredPackages returns the packages required by the Aspire project.
func (host *aspireLanguageHost) GetRequiredPackages(
	ctx context.Context,
	req *pulumirpc.GetRequiredPackagesRequest,
) (*pulumirpc.GetRequiredPackagesResponse, error) {
	logging.V(5).Infof("GetRequiredPackages: %s", req.Info.ProgramDirectory)

	// For now, return an empty list - the actual packages are handled by the .NET SDK
	return &pulumirpc.GetRequiredPackagesResponse{}, nil
}

// RunPlugin executes a plugin program asynchronously.
func (host *aspireLanguageHost) RunPlugin(
	req *pulumirpc.RunPluginRequest,
	server pulumirpc.LanguageRuntime_RunPluginServer,
) error {
	logging.V(5).Infof("RunPlugin: %s", req.Program)

	// Not applicable for Aspire - we don't run plugins in the traditional sense
	return nil
}
