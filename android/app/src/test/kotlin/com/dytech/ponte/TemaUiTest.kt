package com.dytech.ponte

import android.app.AlertDialog
import android.content.res.Configuration
import android.graphics.Color
import android.util.TypedValue
import android.view.View
import android.widget.EditText
import android.widget.Spinner
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import org.robolectric.shadows.ShadowAlertDialog
import org.robolectric.shadows.ShadowPopupMenu

/** Cobre a preferência na tela real, incluindo recursos e widgets nativos. */
@RunWith(org.robolectric.RobolectricTestRunner::class)
@Config(sdk = [35], qualifiers = "w320dp-h640dp-notnight")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class TemaUiTest {
    private val context get() = RuntimeEnvironment.getApplication()

    @Before fun prepara() {
        context.getSharedPreferences("ponte", 0).edit().clear().commit()
        DeviceIdentity.replacement = Apoio.identidade()
        UpdateFlow.automatic = false
    }

    @After fun desfaz() { DeviceIdentity.replacement = null }

    @Test fun automaticoSegueSistemaClaro() = verificaTema(false, "tema-claro-320.png")

    @Test @Config(qualifiers = "w320dp-h640dp-night")
    fun automaticoSegueSistemaEscuro() = verificaTema(true, "tema-escuro-320.png")

    @Test fun escuroPersistidoSobrepoeSistemaClaro() {
        Store(context).themeMode = ThemeMode.DARK
        verificaTema(true)
    }

    @Test @Config(qualifiers = "w320dp-h640dp-night")
    fun claroPersistidoSobrepoeSistemaEscuro() {
        Store(context).themeMode = ThemeMode.LIGHT
        verificaTema(false)
    }

    @Test fun preferenciaDesconhecidaUsaSistema() {
        context.getSharedPreferences("ponte", 0).edit().putString("tema", "desconhecido").commit()
        assertEquals(ThemeMode.SYSTEM, Store(context).themeMode)
        verificaTema(false)
    }

    @Test fun configuracoesSalvamTemaEPreservamEnderecoDigitado() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        val activity = controller.get()
        activity.findViewById<EditText>(R.id.endereco).setText("192.168.0.77")
        abreConfiguracoes(activity)
        val dialog = ShadowAlertDialog.getLatestAlertDialog()
        val spinner = dialog.findViewById<Spinner>(R.id.tema)
        assertEquals(0, spinner.selectedItemPosition)
        spinner.setSelection(2)
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
        Apoio.avanca(20)
        assertEquals(ThemeMode.DARK, Store(context).themeMode)
        // Recriação do Android deve restaurar o endereço ainda não conectado.
        controller.recreate()
        assertEquals("192.168.0.77", controller.get().findViewById<EditText>(R.id.endereco).text.toString())
        assertNight(controller.get(), true)
        abreConfiguracoes(controller.get())
        assertEquals(2, ShadowAlertDialog.getLatestAlertDialog().findViewById<Spinner>(R.id.tema).selectedItemPosition)
        ShadowAlertDialog.getLatestAlertDialog().dismiss()
        controller.pause().stop().destroy()
    }

    @Test fun cancelarNaoMudaTema() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        abreConfiguracoes(controller.get())
        val dialog = ShadowAlertDialog.getLatestAlertDialog()
        dialog.findViewById<Spinner>(R.id.tema).setSelection(2)
        dialog.getButton(AlertDialog.BUTTON_NEGATIVE).performClick()
        Apoio.avanca(20)
        assertEquals(ThemeMode.SYSTEM, Store(context).themeMode)
        controller.pause().stop().destroy()
    }

    @Test @Config(qualifiers = "w320dp-h640dp-night")
    fun voltarAoAutomaticoRecuperaTemaDoSistema() {
        Store(context).themeMode = ThemeMode.LIGHT
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        assertNight(controller.get(), false)
        abreConfiguracoes(controller.get())
        val dialog = ShadowAlertDialog.getLatestAlertDialog()
        dialog.findViewById<Spinner>(R.id.tema).setSelection(0)
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
        Apoio.avanca(20)
        assertEquals(ThemeMode.SYSTEM, Store(context).themeMode)
        controller.recreate()
        assertNight(controller.get(), true)
        controller.pause().stop().destroy()
    }

    private fun verificaTema(dark: Boolean, capture: String? = null) {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        val activity = controller.get()
        assertNight(activity, dark)
        val background = activity.getColor(R.color.fundo)
        val foreground = activity.findViewById<EditText>(R.id.endereco).currentTextColor
        assertTrue("texto precisa contrastar com o fundo", contrast(foreground, background) >= 4.5)
        assertTrue("botão principal precisa ter texto legível", contrast(activity.getColor(R.color.texto_botao), activity.getColor(R.color.botao)) >= 4.5)
        abreConfiguracoes(activity)
        val dialog = ShadowAlertDialog.getLatestAlertDialog()
        val nativeBackground = TypedValue()
        dialog.context.theme.resolveAttribute(android.R.attr.colorBackground, nativeBackground, true)
        assertEquals("diálogo nativo deve acompanhar o tema", dark, Color.luminance(nativeBackground.data) < 0.5f)
        dialog.dismiss()
        if (capture != null) Apoio.captura(activity, capture)
        controller.pause().stop().destroy()
    }

    private fun assertNight(activity: MainActivity, dark: Boolean) {
        assertEquals(if (dark) Configuration.UI_MODE_NIGHT_YES else Configuration.UI_MODE_NIGHT_NO,
            activity.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK)
        assertEquals(dark, Color.luminance(activity.getColor(R.color.fundo)) < 0.5f)
    }

    private fun abreConfiguracoes(activity: MainActivity) {
        activity.findViewById<View>(R.id.menu).performClick()
        assertTrue(ShadowPopupMenu.getLatestPopupMenu().menu.performIdentifierAction(R.id.configuracoes, 0))
    }

    private fun contrast(a: Int, b: Int): Double {
        val first = Color.luminance(a).toDouble()
        val second = Color.luminance(b).toDouble()
        return (maxOf(first, second) + 0.05) / (minOf(first, second) + 0.05)
    }
}
