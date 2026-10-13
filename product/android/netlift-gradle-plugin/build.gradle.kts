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

val prepareNetLiftAndroidPlatform by tasks.registering(Exec::class) {
    inputs.dir(rootProject.file("src/NetLift.AndroidPlatform"))
    outputs.file(
        rootProject.file(
            "src/NetLift.AndroidPlatform/bin/Release/net11.0-android/" +
                "NetLift.AndroidPlatform.dll"
        )
    )
    commandLine(
        "dotnet",
        "build",
        rootProject.file("src/NetLift.AndroidPlatform/NetLift.AndroidPlatform.csproj"),
        "-c",
        "Release",
        "-f",
        "net11.0-android",
        "-v:minimal",
    )
}

tasks.processResources {
    dependsOn(
        prepareNetLiftTool,
        prepareNetLiftRuntime,
        prepareNetLiftAndroidPlatform,
    )
    from(layout.buildDirectory.dir("generated-resources"))
    from(
        rootProject.file(
            "src/NetLift.AndroidPlatform/bin/Release/net11.0-android/" +
                "NetLift.AndroidPlatform.dll"
        )
    ) {
        into("netlift/android-platform")
    }
    from(rootProject.file("tooling")) {
        exclude("**/__pycache__/**", "**/*.pyc")
        into("netlift/tooling")
    }
    from(rootProject.file("../tooling/managed_project.py")) {
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
