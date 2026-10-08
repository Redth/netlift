import org.gradle.api.tasks.Exec

plugins {
    `java-gradle-plugin`
    `maven-publish`
    id("org.jetbrains.kotlin.jvm")
}

group = "io.github.redth.netlift"
version = providers.gradleProperty("netliftPluginVersion").get()

dependencies {
    compileOnly("com.android.tools.build:gradle:8.11.1")
}

kotlin {
    jvmToolchain(17)
}

gradlePlugin {
    plugins {
        create("netlift") {
            id = "io.github.redth.netlift"
            implementationClass = "dev.netlift.gradle.NetLiftPlugin"
            displayName = "NetLift"
            description = "Generates Kotlin projections and packages a .NET Android workload component."
        }
    }
}

val prepareNetLiftTool by tasks.registering(Exec::class) {
    val output = layout.buildDirectory.dir("generated-resources/netlift/tool")
    inputs.dir(rootProject.file("../src"))
    outputs.dir(output)
    commandLine(
        "dotnet",
        "publish",
        rootProject.file("../src/NetLift.Tool/NetLift.Tool.csproj"),
        "-c",
        "Release",
        "--no-self-contained",
        "-o",
        output.get().asFile,
        "-v:minimal",
    )
}

val prepareNetLiftRuntime by tasks.registering(Exec::class) {
    val output = layout.buildDirectory.dir("generated-resources/netlift/runtime")
    inputs.dir(rootProject.file("../src/NetLift.Runtime"))
    outputs.dir(output)
    commandLine(
        "dotnet",
        "publish",
        rootProject.file("../src/NetLift.Runtime/NetLift.Runtime.csproj"),
        "-c",
        "Release",
        "-f",
        "net10.0",
        "--no-self-contained",
        "-o",
        output.get().asFile,
        "-v:minimal",
    )
}

tasks.processResources {
    dependsOn(prepareNetLiftTool, prepareNetLiftRuntime)
    from(layout.buildDirectory.dir("generated-resources"))
    from(rootProject.file("tooling")) {
        into("netlift/tooling")
    }
}

publishing {
    repositories {
        maven {
            name = "preview"
            url = rootProject.layout.projectDirectory.dir("../artifacts/maven").asFile.toURI()
        }
    }
}
