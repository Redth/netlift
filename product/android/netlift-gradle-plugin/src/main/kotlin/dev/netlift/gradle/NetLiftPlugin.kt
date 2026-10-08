package dev.netlift.gradle

import com.android.build.api.variant.AndroidComponentsExtension
import org.gradle.api.Plugin
import org.gradle.api.Project

class NetLiftPlugin : Plugin<Project> {
    override fun apply(project: Project) {
        val extension = project.extensions.create(
            "netlift",
            NetLiftExtension::class.java,
        )
        extension.kotlinPackage.convention("${project.group}.netlift")
        extension.swiftModule.convention("NetLiftContracts")
        extension.contractTargetFramework.convention("net10.0")
        extension.componentTargetFramework.convention("net10.0-android")
        extension.runtimeIdentifier.convention("android-arm64")
        extension.runtimeVersion.convention("0.1.0-alpha.1")
        extension.componentApplicationId.convention("${project.name}.netlift.component")
        extension.generatedDirectory.convention(project.layout.buildDirectory.dir("generated/netlift"))

        project.pluginManager.withPlugin("com.android.application") {
            project.dependencies.add(
                "implementation",
                extension.runtimeVersion.map {
                    "io.github.redth.netlift:runtime-android:$it"
                },
            )
            @Suppress("UNCHECKED_CAST")
            val androidComponents = project.extensions.getByType(
                AndroidComponentsExtension::class.java
            ) as AndroidComponentsExtension<*, *, *>
            androidComponents.onVariants { variant ->
                val capitalized = variant.name.replaceFirstChar(Char::uppercase)
                val configurationName = if (variant.name.contains("release", true)) {
                    "Release"
                } else {
                    "Debug"
                }
                val task = project.tasks.register(
                    "generateNetLift$capitalized",
                    NetLiftGenerateTask::class.java,
                ) { netLiftTask ->
                    netLiftTask.group = "netlift"
                    netLiftTask.description =
                        "Generates NetLift projections and the ${variant.name} workload component."
                    netLiftTask.managedProject.set(extension.managedProject)
                    netLiftTask.componentProject.set(extension.componentProject)
                    netLiftTask.contractRoots.set(extension.contractRoots)
                    netLiftTask.kotlinPackage.set(extension.kotlinPackage)
                    netLiftTask.swiftModule.set(extension.swiftModule)
                    netLiftTask.contractTargetFramework.set(extension.contractTargetFramework)
                    netLiftTask.componentTargetFramework.set(extension.componentTargetFramework)
                    netLiftTask.runtimeIdentifier.set(extension.runtimeIdentifier)
                    netLiftTask.componentApplicationId.set(extension.componentApplicationId)
                    netLiftTask.compatibilityLock.set(extension.compatibilityLock)
                    netLiftTask.configuration.set(configurationName)
                    netLiftTask.runtimeVersion.set(extension.runtimeVersion)
                    netLiftTask.generatedDirectory.set(
                        extension.generatedDirectory.dir(variant.name)
                    )
                    netLiftTask.componentAar.set(
                        project.layout.buildDirectory.file(
                            "netlift/${variant.name}/netlift-workload-component.aar"
                        )
                    )
                    netLiftTask.toolingDirectory.set(
                        project.layout.buildDirectory.dir("netlift/tooling")
                    )
                    netLiftTask.managedSources.from(project.provider {
                        project.fileTree(extension.managedProject.get().asFile.parentFile).matching { patterns ->
                            patterns.include("**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets")
                            patterns.exclude("**/bin/**", "**/obj/**")
                        }
                    })
                    netLiftTask.componentSources.from(project.provider {
                        project.fileTree(extension.componentProject.get().asFile.parentFile).matching { patterns ->
                            patterns.include("**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets")
                            patterns.exclude("**/bin/**", "**/obj/**")
                        }
                    })
                }
                variant.sources.java?.addGeneratedSourceDirectory(
                    task,
                    { it.generatedDirectory },
                )
                project.dependencies.add(
                    "${variant.name}Implementation",
                    project.files(task.flatMap { it.componentAar }).builtBy(task),
                )
            }
        }
    }
}
