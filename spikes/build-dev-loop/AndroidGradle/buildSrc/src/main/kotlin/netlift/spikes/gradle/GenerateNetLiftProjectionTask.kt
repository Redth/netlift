package netlift.spikes.gradle

import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import javax.inject.Inject
import org.gradle.api.DefaultTask
import org.gradle.api.GradleException
import org.gradle.api.file.DirectoryProperty
import org.gradle.api.file.RegularFileProperty
import org.gradle.api.provider.Property
import org.gradle.api.tasks.Input
import org.gradle.api.tasks.InputFile
import org.gradle.api.tasks.Internal
import org.gradle.api.tasks.OutputDirectory
import org.gradle.api.tasks.PathSensitive
import org.gradle.api.tasks.PathSensitivity
import org.gradle.api.tasks.TaskAction
import org.gradle.process.ExecOperations
import org.gradle.work.DisableCachingByDefault

@DisableCachingByDefault(
    because = "This fixture validates local incremental task wiring, not remote build-cache reuse."
)
abstract class GenerateNetLiftProjectionTask @Inject constructor(
    private val execOperations: ExecOperations
) : DefaultTask() {
    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val contractFixture: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val projectionProject: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val projectionSource: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val sdkPin: RegularFileProperty

    @get:Input
    abstract val variantName: Property<String>

    @get:Input
    abstract val dotnetCommand: Property<String>

    @get:Internal
    abstract val dotnetHome: DirectoryProperty

    @get:Internal
    abstract val nugetPackageCache: DirectoryProperty

    @get:OutputDirectory
    abstract val managedArtifactsDirectory: DirectoryProperty

    @get:OutputDirectory
    abstract val kotlinOutputDirectory: DirectoryProperty

    @TaskAction
    fun generate() {
        val sdkFile = sdkPin.get().asFile
        val repositoryRoot = sdkFile.parentFile
        val artifactsDirectory = managedArtifactsDirectory.get().asFile
        val projectionDirectory = kotlinOutputDirectory.get().asFile
        artifactsDirectory.deleteRecursively()
        projectionDirectory.deleteRecursively()
        artifactsDirectory.mkdirs()
        projectionDirectory.mkdirs()

        execOperations.exec {
            workingDir(repositoryRoot)
            commandLine(
                dotnetCommand.get(),
                "build",
                projectionProject.get().asFile.absolutePath,
                "-c",
                "Release",
                "--artifacts-path",
                artifactsDirectory.absolutePath,
                "--nologo"
            )
            environment("DOTNET_CLI_HOME", dotnetHome.get().asFile.absolutePath)
            environment("NUGET_PACKAGES", nugetPackageCache.get().asFile.absolutePath)
            environment("DOTNET_NOLOGO", "true")
            environment("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
        }

        val projectionAssembly = artifactsDirectory.resolve(
            "bin/NetLift.Projection/release/NetLift.Projection.dll"
        )
        if (!projectionAssembly.isFile) {
            throw GradleException("The NetLift.Projection build did not produce $projectionAssembly")
        }

        execOperations.exec {
            workingDir(repositoryRoot)
            commandLine(
                dotnetCommand.get(),
                projectionAssembly.absolutePath,
                contractFixture.get().asFile.absolutePath,
                projectionDirectory.absolutePath
            )
            environment("DOTNET_CLI_HOME", dotnetHome.get().asFile.absolutePath)
            environment("NUGET_PACKAGES", nugetPackageCache.get().asFile.absolutePath)
            environment("DOTNET_NOLOGO", "true")
            environment("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
        }

        val kotlinSource = projectionDirectory.resolve("NetLift.kt")
        val header = projectionDirectory.resolve("netlift.h")
        if (!kotlinSource.isFile || !header.isFile) {
            throw GradleException("Projection output is incomplete for ${variantName.get()}.")
        }

        val expectedHash = Regex("SchemaHash\\s*=\\s*\"([0-9a-f]{64})\"")
            .find(contractFixture.get().asFile.readText())
            ?.groupValues
            ?.get(1)
            ?: throw GradleException("Fixture does not declare its canonical schema hash.")
        val headerHash = Regex("#define LIFT_SCHEMA_HASH_HEX \"([0-9a-f]{64})\"")
            .find(header.readText())
            ?.groupValues
            ?.get(1)
            ?: throw GradleException("The generated C header does not declare its schema hash.")
        if (headerHash != expectedHash) {
            throw GradleException(
                "Generated schema hash $headerHash does not match fixture hash $expectedHash."
            )
        }

        val kotlinHash = MessageDigest.getInstance("SHA-256")
            .digest(kotlinSource.readBytes())
            .joinToString("") { "%02x".format(it.toInt() and 0xff) }
        val manifest = projectionDirectory.resolve("projection-manifest.json")
        val stagedManifest = projectionDirectory.resolve("projection-manifest.json.tmp")
        stagedManifest.writeText(
            """{"variant":"${variantName.get()}","schemaHash":"$expectedHash","kotlinSha256":"$kotlinHash"}""" +
                "\n"
        )
        Files.move(
            stagedManifest.toPath(),
            manifest.toPath(),
            StandardCopyOption.ATOMIC_MOVE,
            StandardCopyOption.REPLACE_EXISTING
        )
        logger.lifecycle(
            "NETLIFT-PROJECTION: ${variantName.get()} schema sha256=$expectedHash kotlin sha256=$kotlinHash"
        )
    }
}
