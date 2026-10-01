plugins {
    `kotlin-dsl`
}

layout.buildDirectory.set(
    layout.projectDirectory.dir("../../.artifacts/android-gradle/build/buildSrc")
)

repositories {
    gradlePluginPortal()
}
