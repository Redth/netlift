package netlift.spikes.gradle

import java.io.File
import java.io.IOException
import javax.inject.Inject
import org.gradle.api.DefaultTask
import org.gradle.api.GradleException
import org.gradle.api.file.ConfigurableFileCollection
import org.gradle.api.file.DirectoryProperty
import org.gradle.api.file.RegularFileProperty
import org.gradle.api.provider.Property
import org.gradle.api.tasks.Input
import org.gradle.api.tasks.InputFile
import org.gradle.api.tasks.InputFiles
import org.gradle.api.tasks.OutputDirectory
import org.gradle.api.tasks.PathSensitive
import org.gradle.api.tasks.PathSensitivity
import org.gradle.api.tasks.TaskAction
import org.gradle.process.ExecOperations

abstract class StageV5AsyncCodecTask @Inject constructor(
    private val execOperations: ExecOperations
) : DefaultTask() {
    @get:Input
    abstract val direction: Property<String>

    @get:Input
    abstract val variantName: Property<String>

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val codecLock: RegularFileProperty

    @get:InputFiles
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val generatedCodecs: ConfigurableFileCollection

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val consumerTemplate: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val stageScript: RegularFileProperty

    @get:Input
    abstract val sourceDirectoryPath: Property<String>

    @get:OutputDirectory
    abstract val kotlinOutputDirectory: DirectoryProperty

    @TaskAction
    fun stage() {
        val selected = direction.get()
        if (selected != "old" && selected != "new") {
            throw GradleException("Unsupported async codec direction: $selected")
        }
        val output = kotlinOutputDirectory.get().asFile
        clearOutput(output, variantName.get())
        try {
            execOperations.exec {
                commandLine(
                    "python3",
                    stageScript.get().asFile.absolutePath,
                    codecLock.get().asFile.absolutePath,
                    selected,
                    "kotlin",
                    sourceDirectoryPath.get(),
                    consumerTemplate.get().asFile.absolutePath,
                    output.absolutePath
                )
            }
            val codec = output.resolve("NetLift.kt")
            val consumer = output.resolve("CodecConsumer.kt")
            val manifest = output.resolve("async-codec-provenance.json")
            if (!codec.isFile || !consumer.isFile || !manifest.isFile) {
                throw GradleException(
                    "Async codec staging outputs are incomplete for ${variantName.get()}."
                )
            }
            val codecHash = java.security.MessageDigest.getInstance("SHA-256")
                .digest(codec.readBytes())
                .joinToString("") { "%02x".format(it.toInt() and 0xff) }
            logger.lifecycle(
                "NETLIFT-V5-ASYNC-CODEC: ${variantName.get()} " +
                    "direction=$selected sha256=$codecHash"
            )
        } catch (error: Exception) {
            try {
                clearOutput(output, variantName.get())
            } catch (cleanupError: GradleException) {
                error.addSuppressed(cleanupError)
            }
            if (error is GradleException) throw error
            throw GradleException(
                "Could not stage async codec for ${variantName.get()}.",
                error
            )
        }
    }

    private fun clearOutput(output: File, variant: String) {
        if (output.exists() && !output.deleteRecursively()) {
            throw GradleException("Could not remove incomplete async codec output for $variant.")
        }
    }
}
