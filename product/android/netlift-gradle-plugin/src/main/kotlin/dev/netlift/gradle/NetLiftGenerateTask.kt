package dev.netlift.gradle

import org.gradle.api.DefaultTask
import org.gradle.api.file.ConfigurableFileCollection
import org.gradle.api.file.DirectoryProperty
import org.gradle.api.file.RegularFileProperty
import org.gradle.api.provider.ListProperty
import org.gradle.api.provider.Property
import org.gradle.api.tasks.Input
import org.gradle.api.tasks.InputFile
import org.gradle.api.tasks.InputFiles
import org.gradle.api.tasks.Internal
import org.gradle.api.tasks.Optional
import org.gradle.api.tasks.OutputDirectory
import org.gradle.api.tasks.OutputFile
import org.gradle.api.tasks.PathSensitive
import org.gradle.api.tasks.PathSensitivity
import org.gradle.api.tasks.TaskAction
import org.gradle.process.ExecOperations
import java.io.File
import java.net.URI
import java.nio.file.FileSystems
import java.nio.file.Files
import javax.inject.Inject

abstract class NetLiftGenerateTask @Inject constructor(
    private val execOperations: ExecOperations,
) : DefaultTask() {
    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val managedProject: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val componentProject: RegularFileProperty

    @get:InputFiles
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val managedSources: ConfigurableFileCollection

    @get:InputFiles
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val componentSources: ConfigurableFileCollection

    @get:Input
    abstract val contractRoots: ListProperty<String>

    @get:Input
    abstract val kotlinPackage: Property<String>

    @get:Input
    abstract val swiftModule: Property<String>

    @get:Input
    abstract val contractTargetFramework: Property<String>

    @get:Input
    abstract val componentTargetFramework: Property<String>

    @get:Input
    abstract val runtimeIdentifier: Property<String>

    @get:Input
    abstract val componentApplicationId: Property<String>

    @get:Input
    abstract val configuration: Property<String>

    @get:Input
    abstract val runtimeVersion: Property<String>

    @get:Optional
    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val compatibilityLock: RegularFileProperty

    @get:OutputDirectory
    abstract val generatedDirectory: DirectoryProperty

    @get:OutputFile
    abstract val componentAar: RegularFileProperty

    @get:Internal
    abstract val toolingDirectory: DirectoryProperty

    @TaskAction
    fun generate() {
        val tooling = toolingDirectory.get().asFile
        extractBundledTooling(tooling)
        val managed = managedProject.get().asFile
        val component = componentProject.get().asFile
        val generated = generatedDirectory.get().asFile
        val configurationName = configuration.get()
        val managedAssembly = managed.parentFile
            .resolve("bin/$configurationName/${contractTargetFramework.get()}/${managed.nameWithoutExtension}.dll")

        execOperations.exec { spec ->
            spec.workingDir(managed.parentFile)
            spec.commandLine(
                "dotnet",
                "build",
                managed,
                "-c",
                configurationName,
                "-f",
                contractTargetFramework.get(),
                "-v:minimal",
            )
        }

        generated.mkdirs()
        val config = temporaryDir.resolve("netlift.json")
        val lockLine = compatibilityLock.orNull?.asFile?.let {
            ",\n  \"compatibilityLock\": \"${jsonPath(it)}\""
        }.orEmpty()
        config.writeText(
            """{
  "version": 1,
  "assembly": "${jsonPath(managedAssembly)}",
  "contractRoots": [${contractRoots.get().joinToString(", ") { "\"${json(it)}\"" }}],
  "kotlinPackage": "${json(kotlinPackage.get())}",
  "swiftModule": "${json(swiftModule.get())}",
  "output": "${jsonPath(generated)}"$lockLine
}
"""
        )
        execOperations.exec { spec ->
            spec.workingDir(managed.parentFile)
            spec.commandLine(
                "dotnet",
                tooling.resolve("tool/NetLift.Tool.dll"),
                config,
            )
        }
        execOperations.exec { spec ->
            spec.workingDir(component.parentFile)
            spec.commandLine(
                "python3",
                tooling.resolve("tooling/build_component.py"),
                "--project",
                component,
                "--generated",
                generated,
                "--output",
                componentAar.get().asFile,
                "--runtime-version",
                runtimeVersion.get(),
                "--configuration",
                configurationName,
                "--target-framework",
                componentTargetFramework.get(),
                "--runtime-identifier",
                runtimeIdentifier.get(),
                "--application-id",
                componentApplicationId.get(),
            )
        }
    }

    private fun extractBundledTooling(destination: File) {
        destination.deleteRecursively()
        destination.mkdirs()
        val source = File(NetLiftGenerateTask::class.java.protectionDomain.codeSource.location.toURI())
        if (source.isDirectory) {
            source.resolve("netlift").copyRecursively(destination, overwrite = true)
            return
        }
        FileSystems.newFileSystem(URI.create("jar:${source.toURI()}"), emptyMap<String, Any>()).use { fs ->
            val root = fs.getPath("/netlift")
            Files.walk(root).use { paths ->
                paths.forEach { path ->
                    val relative = root.relativize(path).toString()
                    if (relative.isEmpty()) return@forEach
                    val target = destination.toPath().resolve(relative)
                    if (Files.isDirectory(path)) Files.createDirectories(target)
                    else {
                        Files.createDirectories(target.parent)
                        Files.copy(path, target)
                    }
                }
            }
        }
    }

    private fun jsonPath(file: File): String = json(file.absolutePath)

    private fun json(value: String): String =
        value.replace("\\", "\\\\").replace("\"", "\\\"")
}
