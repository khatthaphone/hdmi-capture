pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "HDMICaptain"
include(
    ":app",
    ":feature:viewer",
    ":feature:settings",
    ":core:model",
    ":core:capture",
    ":core:render",
    ":core:audio",
    ":core:billing",
    ":core:prefs",
)
