using Docker.DotNet;
using Docker.DotNet.Models;
using System.Text;

namespace KopiaMonitorApp.Services;

public class DockerExecService
{
    private readonly DockerClient _client;

    public DockerExecService()
    {
        // Si collega al socket Docker predefinito di Linux
        _client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
    }

    public async Task<string> RunKopiaCommandAsync(string containerName, string command)
    {
        try
        {
            var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters());
            var container = containers.FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == containerName));

            if (container == null)
            {
                throw new Exception($"Container '{containerName}' non trovato o non in esecuzione.");
            }

            var execCreateResponse = await _client.Exec.ExecCreateContainerAsync(container.ID, new ContainerExecCreateParameters
            {
                Cmd = new[] { "sh", "-c", command },
                AttachStdout = true,
                AttachStderr = true
            });

            using var stream = await _client.Exec.StartAndAttachContainerExecAsync(execCreateResponse.ID, false);
            var (stdout, stderr) = await stream.ReadOutputToEndAsync(CancellationToken.None);

            return $"{stdout}\n{stderr}";
        }
        catch (Exception ex)
        {
            return $"[ERRORE DOCKER EXEC]: {ex.Message}";
        }
    }
}
