package com.dytech.ponte

import android.view.KeyEvent
import android.view.inputmethod.EditorInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/** O que o teclado do celular produz vira texto ou teclas para o computador. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class TecladoTest {
    private val recorder = Recorder()
    private val keyboard = Keyboard { recorder }

    @Test
    fun textoSegueComoTexto() {
        keyboard.type("Ação, R\$ 10 €")
        assertEquals(listOf("texto Ação, R\$ 10 €"), recorder.take())
    }

    @Test
    fun ctrlArmadoTransformaALetraEmAtalho() {
        keyboard.toggleArmed(Vk.CONTROL)
        assertTrue(keyboard.isArmed(Vk.CONTROL))
        keyboard.type("c")
        assertEquals(listOf("tecla 0x11 aperta", "tecla 0x43 aperta", "tecla 0x43 solta", "tecla 0x11 solta"), recorder.take())
        assertFalse("o Ctrl vale para uma tecla só", keyboard.isArmed(Vk.CONTROL))
        // Sem nada armado, a mesma letra volta a ser texto.
        keyboard.type("c")
        assertEquals(listOf("texto c"), recorder.take())
    }

    @Test
    fun teclasDeNavegacaoVaoMarcadasComoEstendidas() {
        keyboard.tap(Vk.LEFT)
        keyboard.tap(Vk.ESCAPE)
        assertEquals(listOf("tecla 0x25 aperta ext", "tecla 0x25 solta ext", "tecla 0x1B aperta", "tecla 0x1B solta"), recorder.take())
    }

    @Test
    fun atalhoSoltaNaOrdemInversa() {
        keyboard.combo(Vk.CONTROL, Vk.SHIFT, Vk.ESCAPE)
        assertEquals(
            listOf("tecla 0x11 aperta", "tecla 0x10 aperta", "tecla 0x1B aperta", "tecla 0x1B solta", "tecla 0x10 solta", "tecla 0x11 solta"),
            recorder.take(),
        )
    }

    @Test
    fun computadorAntigoRecebeSoOQueTemTeclaCerta() {
        recorder.acceptsText = false
        var notice = ""
        keyboard.onNotice = { notice = it }
        keyboard.type("Ab1é")
        assertEquals(
            listOf(
                "tecla 0x10 aperta", "tecla 0x41 aperta", "tecla 0x41 solta", "tecla 0x10 solta",
                "tecla 0x42 aperta", "tecla 0x42 solta",
                "tecla 0x31 aperta", "tecla 0x31 solta",
            ),
            recorder.take(),
        )
        assertTrue("faltou avisar que o acento não foi", notice.contains("antigo"))
    }

    @Test
    fun tecladoVirtualPelaConexaoDeEntrada() {
        val view = RemoteView(RuntimeEnvironment.getApplication())
        view.keyboard = keyboard
        val info = EditorInfo()
        val input = view.onCreateInputConnection(info)
        assertTrue("a tela precisa se declarar editável para o teclado abrir", view.onCheckIsTextEditor())

        input.commitText("o", 1)
        assertEquals(listOf("texto o"), recorder.take())

        // Teclado que "compõe" a palavra: só a diferença segue.
        input.setComposingText("ola", 1)
        assertEquals(listOf("texto ola"), recorder.take())
        input.setComposingText("olá", 1)
        assertEquals(listOf("tecla 0x08 aperta", "tecla 0x08 solta", "texto á"), recorder.take())
        input.commitText("olá ", 1)
        assertEquals(listOf("texto  "), recorder.take())

        // Apagar e Enter chegam como eventos de tecla.
        input.deleteSurroundingText(2, 0)
        assertEquals(listOf("tecla 0x08 aperta", "tecla 0x08 solta", "tecla 0x08 aperta", "tecla 0x08 solta"), recorder.take())
        input.sendKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_DEL))
        input.sendKeyEvent(KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_DEL))
        input.sendKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_ENTER))
        input.sendKeyEvent(KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_ENTER))
        assertEquals(listOf("tecla 0x08 aperta", "tecla 0x08 solta", "tecla 0x0D aperta", "tecla 0x0D solta"), recorder.take())

        // Emoji ocupa duas posições no texto e não pode ser cortado ao meio.
        input.setComposingText("a😀", 1)
        input.setComposingText("a😁", 1)
        assertEquals(listOf("texto a😀", "tecla 0x08 aperta", "tecla 0x08 solta", "texto 😁"), recorder.take())
    }

    @Test
    fun botaoVoltarNaoVaiParaOComputador() {
        assertFalse(keyboard.onKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BACK)))
        assertEquals(emptyList<String>(), recorder.take())
    }
}
