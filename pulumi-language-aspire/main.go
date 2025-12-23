// Copyright 2024, EmmittJ. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// pulumi-language-aspire is the Pulumi language host for .NET Aspire applications.
// It delegates to the Aspire CLI for deployment operations.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	pbempty "github.com/golang/protobuf/ptypes/empty"
	"github.com/pulumi/pulumi/sdk/v3/go/common/util/logging"
	"github.com/pulumi/pulumi/sdk/v3/go/common/util/rpcutil"
	pulumirpc "github.com/pulumi/pulumi/sdk/v3/proto/go"
	"google.golang.org/grpc"
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
	if len(args) == 0 {
		fmt.Fprintf(os.Stderr, "usage: pulumi-language-aspire <engine-address>\n")
		os.Exit(1)
	}

	engineAddress := args[0]
	if err := run(engineAddress, tracing); err != nil {
		fmt.Fprintf(os.Stderr, "error: %v\n", err)
		os.Exit(1)
	}
}

func run(engineAddress, tracing string) error {
	// Connect to the engine
	conn, err := grpc.Dial(
		engineAddress,
		grpc.WithInsecure(),
		rpcutil.GrpcChannelOptions(),
	)
	if err != nil {
		return fmt.Errorf("failed to connect to engine: %w", err)
	}
	defer conn.Close()

	// Create the language host
	host := &aspireLanguageHost{
		engineAddress: engineAddress,
		tracing:       tracing,
	}

	// Start the gRPC server
	port, done, err := rpcutil.Serve(0, nil, []func(*grpc.Server) error{
		func(srv *grpc.Server) error {
			pulumirpc.RegisterLanguageRuntimeServer(srv, host)
			return nil
		},
	}, nil)
	if err != nil {
		return fmt.Errorf("failed to start language host: %w", err)
	}

	// Print the port for the engine to connect
	fmt.Printf("%d\n", port)

	// Wait for the server to stop
	<-done
	return nil
}

// aspireLanguageHost implements the Pulumi language runtime for Aspire.
type aspireLanguageHost struct {
	pulumirpc.UnimplementedLanguageRuntimeServer
	engineAddress string
	tracing       string
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
	logging.V(5).Infof("Run: %s", req.Program)

	// Find the Aspire project directory
	projectDir := req.Program
	if projectDir == "" {
		projectDir = req.Pwd
	}

	// Run `aspire deploy` which will invoke our SDK
	cmd := exec.CommandContext(ctx, "aspire", "deploy")
	cmd.Dir = projectDir

	// Pass Pulumi environment variables
	cmd.Env = append(os.Environ(),
		fmt.Sprintf("PULUMI_ENGINE_ADDRESS=%s", host.engineAddress),
		fmt.Sprintf("PULUMI_MONITOR_ADDRESS=%s", req.MonitorAddress),
		fmt.Sprintf("PULUMI_STACK=%s", req.Stack),
		fmt.Sprintf("PULUMI_PROJECT=%s", req.Project),
		fmt.Sprintf("PULUMI_PARALLEL=%d", req.Parallel),
		"PULUMI_RUNTIME=aspire",
	)

	// Add config as environment variables
	for key, value := range req.Config {
		envKey := fmt.Sprintf("PULUMI_CONFIG_%s", strings.ReplaceAll(strings.ToUpper(key), ":", "_"))
		cmd.Env = append(cmd.Env, fmt.Sprintf("%s=%s", envKey, value))
	}

	// Capture output
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	// Run the command
	if err := cmd.Run(); err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			return &pulumirpc.RunResponse{
				Error: fmt.Sprintf("aspire deploy failed with exit code %d", exitErr.ExitCode()),
			}, nil
		}
		return &pulumirpc.RunResponse{
			Error: fmt.Sprintf("failed to run aspire deploy: %v", err),
		}, nil
	}

	return &pulumirpc.RunResponse{}, nil
}

// GetPluginInfo returns information about this language plugin.
func (host *aspireLanguageHost) GetPluginInfo(
	ctx context.Context,
	req *pbempty.Empty,
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
	req *pbempty.Empty,
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
