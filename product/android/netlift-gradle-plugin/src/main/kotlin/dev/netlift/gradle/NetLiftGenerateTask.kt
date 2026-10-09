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
import java.util.jar.JarFile
import javax.inject.Inject

abstract class NetLiftGenerateTask @Inject constructor(
    private val execOperations: ExecOperations,
) : DefaultTask() {
    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val managedProject: RegularFileProperty

    @get:InputFiles
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val managedSources: ConfigurableFileCollection

    @get:Input
    abstract val contractRoots: ListProperty<String>

    @get:Input
    abstract val nativeContractRoots: ListProperty<String>

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
        val nativeRootsLine = if (nativeContractRoots.get().isEmpty()) {
            ""
        } else {
            ",\n  \"nativeContractRoots\": [" +
                nativeContractRoots.get().joinToString(", ") { "\"${json(it)}\"" } +
                "]"
        }
        config.writeText(
            """{
  "version": 1,
  "assembly": "${jsonPath(managedAssembly)}",
  "contractRoots": [${contractRoots.get().joinToString(", ") { "\"${json(it)}\"" }}],
  "kotlinPackage": "${json(kotlinPackage.get())}",
  "swiftModule": "${json(swiftModule.get())}",
  "output": "${jsonPath(generated)}",
  "emitAndroidBridge": true$lockLine$nativeRootsLine
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
        val component = createComponentProject(
            temporaryDir.resolve("component"),
            managed,
            generated,
            tooling.resolve("runtime/NetLift.Runtime.dll"),
            tooling.resolve("android-platform/NetLift.AndroidPlatform.dll"),
        )
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

    private fun createComponentProject(
        directory: File,
        managedProjectFile: File,
        generated: File,
        runtimeAssembly: File,
        androidPlatformAssembly: File,
    ): File {
        require(runtimeAssembly.isFile) {
            "Bundled NetLift managed runtime was not found: $runtimeAssembly"
        }
        require(androidPlatformAssembly.isFile) {
            "Bundled NetLift Android platform adapter was not found: $androidPlatformAssembly"
        }
        directory.mkdirs()
        return directory.resolve("NetLift.Generated.Android.csproj").also { projectFile ->
            projectFile.writeText(
                """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>${xml(componentTargetFramework.get())}</TargetFramework>
    <OutputType>Exe</OutputType>
    <RuntimeIdentifier>${xml(runtimeIdentifier.get())}</RuntimeIdentifier>
    <ApplicationId>${xml(componentApplicationId.get())}</ApplicationId>
    <ApplicationVersion>1</ApplicationVersion>
    <ApplicationDisplayVersion>0.1</ApplicationDisplayVersion>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <UseMonoRuntime>false</UseMonoRuntime>
    <EnablePreviewFeatures>true</EnablePreviewFeatures>
    <EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <TrimMode>copy</TrimMode>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${xml(managedProjectFile.absolutePath)}" />
    <Reference Include="NetLift.Runtime">
      <HintPath>${xml(runtimeAssembly.absolutePath)}</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="NetLift.AndroidPlatform">
      <HintPath>${xml(androidPlatformAssembly.absolutePath)}</HintPath>
      <Private>true</Private>
    </Reference>
    <Compile Include="${xml(generated.resolve("NetLiftDispatcher.g.cs").absolutePath)}"
             Link="Generated/NetLiftDispatcher.g.cs" />
    <Compile Include="${xml(generated.resolve("NetLiftAndroidBridge.g.cs").absolutePath)}"
             Link="Generated/NetLiftAndroidBridge.g.cs" />
  </ItemGroup>
</Project>
"""
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
        JarFile(source).use { jar ->
            val entries = jar.entries()
            while (entries.hasMoreElements()) {
                val entry = entries.nextElement()
                if (!entry.name.startsWith("netlift/") || entry.name == "netlift/")
                    continue
                val relative = entry.name.removePrefix("netlift/")
                val target = destination.resolve(relative)
                if (entry.isDirectory) {
                    target.mkdirs()
                } else {
                    target.parentFile.mkdirs()
                    jar.getInputStream(entry).use { input ->
                        target.outputStream().use(input::copyTo)
                    }
                }
            }
        }
    }

    private fun jsonPath(file: File): String = json(file.absolutePath)

    private fun json(value: String): String =
        value.replace("\\", "\\\\").replace("\"", "\\\"")

    private fun xml(value: String): String =
        value.replace("&", "&amp;")
            .replace("\"", "&quot;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
}
