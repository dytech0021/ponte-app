package com.dytech.ponte

import android.content.res.Configuration

/** Valores estáveis nas preferências; a ordem é a mesma do seletor de aparência. */
enum class ThemeMode(val value: String) {
    SYSTEM("system"), LIGHT("light"), DARK("dark");

    fun overrideConfiguration(): Configuration? = when (this) {
        SYSTEM -> null // Sem override: mudanças do tema do Android recriam a Activity normalmente.
        LIGHT -> Configuration().apply { uiMode = Configuration.UI_MODE_NIGHT_NO }
        DARK -> Configuration().apply { uiMode = Configuration.UI_MODE_NIGHT_YES }
    }
}
