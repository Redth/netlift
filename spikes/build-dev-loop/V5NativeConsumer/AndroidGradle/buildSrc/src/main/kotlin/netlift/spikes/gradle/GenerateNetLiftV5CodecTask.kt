package netlift.spikes.gradle

import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
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
    because = "This spike measures local native-source integration, not remote build-cache reuse."
)
abstract class GenerateNetLiftV5CodecTask @Inject constructor(
    private val execOperations: ExecOperations
) : DefaultTask() {
    @get:Input
    abstract val selectedDirection: Property<String>

    @get:Input
    abstract val variantName: Property<String>

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val codecSchemaLock: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val contractSource: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.NONE)
    abstract val projectionAssembly: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.NONE)
    abstract val projectionSourceFingerprint: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val stageScript: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val consumerTemplate: RegularFileProperty

    @get:Internal
    abstract val repositoryRootPath: Property<String>

    @get:Internal
    abstract val artifactsRootPath: Property<String>

    @get:OutputDirectory
    abstract val kotlinOutputDirectory: DirectoryProperty

    @TaskAction
    fun generate() {
        val direction = selectedDirection.get()
        if (direction != "old" && direction != "new") {
            throw GradleException("Unsupported schema-v5 projection direction: $direction")
        }
        val output = kotlinOutputDirectory.get().asFile
        clearOutput(output, variantName.get())
        execOperations.exec {
            workingDir(repositoryRootPath.get())
            commandLine(
                "bash",
                stageScript.get().asFile.absolutePath,
                direction,
                "kotlin",
                output.absolutePath,
                "gradle-${variantName.get()}"
            )
            environment("NETLIFT_NATIVE_GRAPH_ARTIFACTS", artifactsRootPath.get())
        }

        try {
            val source = output.resolve("NetLift.kt")
            val provenance = output.resolve("codec-provenance.json")
            if (!source.isFile || !provenance.isFile) {
                throw GradleException(
                    "Schema-v5 Kotlin source or provenance manifest is missing for ${variantName.get()}."
                )
            }
            val packageName = Regex("(?m)^package ([A-Za-z0-9_.]+)$")
                .find(source.readText())
                ?.groupValues
                ?.get(1)
                ?: throw GradleException(
                    "Generated schema-v5 Kotlin source has no package declaration."
                )
            val template = consumerTemplate.get().asFile.readText()
            val packageToken = "__V5_CODEC_PACKAGE__"
            if (!template.contains(packageToken) ||
                template.indexOf(packageToken) != template.lastIndexOf(packageToken)) {
                throw GradleException(
                    "Schema-v5 Kotlin consumer template lacks its package placeholder."
                )
            }
            val consumerText = template.replace("__V5_CODEC_PACKAGE__", packageName)
            val consumer = output.resolve("CodecConsumer.kt")
            val consumerStage = output.resolve("CodecConsumer.kt.tmp")
            consumerStage.writeText(consumerText)
            Files.move(
                consumerStage.toPath(),
                consumer.toPath(),
                StandardCopyOption.ATOMIC_MOVE,
                StandardCopyOption.REPLACE_EXISTING
            )
        } catch (error: GradleException) {
            try {
                clearOutput(output, variantName.get())
            } catch (cleanupError: GradleException) {
                error.addSuppressed(cleanupError)
            }
            throw error
        } catch (error: IOException) {
            clearOutput(output, variantName.get())
            throw GradleException("Could not stage the schema-v5 Kotlin consumer.", error)
        }
        val source = output.resolve("NetLift.kt")
        val provenance = output.resolve("codec-provenance.json")
        val manifest = try {
            provenance.readText()
        } catch (error: IOException) {
            clearOutput(output, variantName.get())
            throw GradleException("Could not read the schema-v5 provenance manifest.", error)
        }
        if (!manifest.contains("\"direction\":\"$direction\"") ||
            !manifest.contains("\"language\":\"kotlin\"")) {
            clearOutput(output, variantName.get())
            throw GradleException("Schema-v5 Kotlin provenance does not match ${variantName.get()}.")
        }
        val sourceBytes = try {
            source.readBytes()
        } catch (error: IOException) {
            clearOutput(output, variantName.get())
            throw GradleException("Could not read the generated schema-v5 Kotlin source.", error)
        }
        val sourceHash = java.security.MessageDigest.getInstance("SHA-256")
            .digest(sourceBytes)
            .joinToString("") { "%02x".format(it.toInt() and 0xff) }
        logger.lifecycle(
            "NETLIFT-V5-CODEC: ${variantName.get()} direction=$direction sha256=$sourceHash"
        )
    }

    private fun clearOutput(output: File, variant: String) {
        if (output.exists() && !output.deleteRecursively()) {
            throw GradleException(
                "Could not remove incomplete schema-v5 output for $variant."
            )
        }
    }
}
