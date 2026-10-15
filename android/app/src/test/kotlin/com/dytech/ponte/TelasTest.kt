package com.dytech.ponte

import android.app.AlertDialog
import android.content.Intent
import android.graphics.Color
import android.view.View
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import org.robolectric.shadows.ShadowAlertDialog
import ponte.core.Target

/**
 * Abre as telas de verdade num Android simulado e grava a imagem delas em capturas/android.
 *
 * O teste da sessão só roda com PONTE_TESTE_ALVO e PONTE_TESTE_SENHA (veja testar-com-o-pc.ps1): ele
 * conecta num Ponte do Windows, responde à pergunta da senha e espera a imagem do computador aparecer.
 * Nada é clicado nem digitado lá.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], qualifiers = "w411dp-h891dp-420dpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class TelasTest {
    private val context get() = RuntimeEnvironment.getApplication()

    @Before
    fun prepara() {
        DeviceIdentity.replacement = Apoio.identidade()
        UpdateFlow.automatic = false
    }

    @After
    fun desfaz() {
        DeviceIdentity.replacement = null
    }

    @Test
    fun telaInicialListaSalvosERecentes() {
        Store(context).apply {
            saveComputer("482 913 507", "Escritório", true)
            saveComputer("731204958", "Casa", true)
            saveComputer("192.168.0.50", "Servidor da loja", false)
            recordConnection(Target.parse("482913507"), "PC-ESCRITORIO", "AA", true)
            recordConnection(Target.parse("615 880 342"), "NOTEBOOK-ANA", "BB", false)
        }
        val activity = Robolectric.buildActivity(MainActivity::class.java).setup().get()
        assertTrue("o endereço deste aparelho não apareceu", Apoio.espera(5000) { activity.findViewById<TextView>(R.id.versao).text.contains("este aparelho") })

        val saved = activity.findViewById<LinearLayout>(R.id.salvos)
        assertEquals(3, saved.childCount)
        // Em ordem alfabética, com o estado da autorização ao lado.
        assertEquals(listOf("Casa", "Escritório", "Servidor da loja"), (0 until 3).map { saved.getChildAt(it).findViewById<TextView>(R.id.nome).text.toString() })
        assertEquals("entra direto", saved.getChildAt(1).findViewById<TextView>(R.id.estado).text.toString())
        assertEquals("pede aprovação", saved.getChildAt(0).findViewById<TextView>(R.id.estado).text.toString())
        // Só o recente que não está salvo aparece na lista de recentes.
        val recents = activity.findViewById<LinearLayout>(R.id.recentes)
        assertEquals(1, recents.childCount)
        assertEquals("NOTEBOOK-ANA", recents.getChildAt(0).findViewById<TextView>(R.id.nome).text.toString())
        assertEquals(View.GONE, activity.findViewById<View>(R.id.salvosVazio).visibility)
        Apoio.captura(activity, "inicial.png")

        // O botão Salvar abre a janela com o endereço digitado.
        activity.findViewById<EditText>(R.id.endereco).setText("123 456 789")
        activity.findViewById<View>(R.id.salvar).performClick()
        val dialog = ShadowAlertDialog.getLatestAlertDialog()
        assertNotNull("a janela de salvar não abriu", dialog)
        Apoio.espera(500) { dialog.isShowing }
        val fields = ArrayList<EditText>()
        collect(dialog.window!!.decorView, fields)
        assertEquals("123 456 789", fields[1].text.toString())
        fields[0].setText("Loja")
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
        assertEquals("Loja", Store(context).findSaved("id:123456789")?.name)
        assertEquals(4, saved.childCount)
    }

    @Test
    fun telaInicialVazia() {
        val activity = Robolectric.buildActivity(MainActivity::class.java).setup().get()
        Apoio.espera(2000) { activity.findViewById<TextView>(R.id.versao).text.contains("este aparelho") }
        assertEquals(View.VISIBLE, activity.findViewById<View>(R.id.salvosVazio).visibility)
        assertEquals(View.GONE, activity.findViewById<View>(R.id.recentesTitulo).visibility)
        Apoio.captura(activity, "inicial-vazia.png")
    }

    @Test
    fun sessaoQueNaoConectaAvisaEFecha() {
        // Do outro lado há algo que atende e desliga na hora, sem ser um Ponte.
        val server = java.net.ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress())
        val accepting = Thread {
            try { server.accept().close() } catch (e: java.io.IOException) { }
        }.apply { start() }
        try {
            val intent = Intent(context, SessionActivity::class.java).putExtra(SessionActivity.EXTRA_TARGET, "127.0.0.1:${server.localPort}")
            val activity = Robolectric.buildActivity(SessionActivity::class.java, intent).setup().get()
            assertTrue("faltou o aviso de falha", Apoio.espera(20000) { ShadowAlertDialog.getLatestAlertDialog()?.isShowing == true })
            val message = ShadowAlertDialog.getLatestAlertDialog().findViewById<TextView>(android.R.id.message).text.toString()
            println("aviso mostrado: $message")
            assertTrue(message, message == "Falha ao estabelecer a conexão segura." || message == "A conexão caiu durante a identificação.")
            ShadowAlertDialog.getLatestAlertDialog().getButton(AlertDialog.BUTTON_POSITIVE).performClick()
            assertTrue("fechar o aviso deveria fechar a tela", Apoio.espera(1000) { activity.isFinishing })
        } finally {
            server.close()
            accepting.join(2000)
        }
    }

    @Test
    fun sessaoComUmPonteDeVerdade() = abreSessao("sessao")

    // O uso mais comum: aparelho deitado, com a tela do computador ocupando quase tudo.
    @Test
    @Config(qualifiers = "w891dp-h411dp-land-420dpi")
    fun sessaoComOAparelhoDeitado() = abreSessao("sessao-deitada")

    private fun abreSessao(name: String) {
        val alvo = System.getenv("PONTE_TESTE_ALVO") ?: ""
        val senha = System.getenv("PONTE_TESTE_SENHA") ?: ""
        assumeTrue("sem PONTE_TESTE_ALVO: sessão de verdade pulada", alvo.isNotEmpty())

        val intent = Intent(context, SessionActivity::class.java).putExtra(SessionActivity.EXTRA_TARGET, alvo)
        val controller = Robolectric.buildActivity(SessionActivity::class.java, intent).setup()
        val activity = controller.get()

        // O computador tem senha e não conhece este aparelho: a pergunta tem de aparecer.
        assertTrue("a pergunta da senha não apareceu", Apoio.espera(20000) { ShadowAlertDialog.getLatestAlertDialog()?.isShowing == true })
        val dialog = ShadowAlertDialog.getLatestAlertDialog()
        Apoio.captura(activity, "$name-conectando.png")
        val fields = ArrayList<EditText>()
        collect(dialog.window!!.decorView, fields)
        fields.single().setText(senha)
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()

        // A primeira imagem do computador some com o texto de estado.
        val status = activity.findViewById<View>(R.id.estado)
        assertTrue("a imagem do computador não chegou", Apoio.espera(30000) { status.visibility == View.GONE })
        assertEquals(View.VISIBLE, activity.findViewById<View>(R.id.barraRolagem).visibility)
        assertTrue("a barra de comandos ficou vazia", activity.findViewById<LinearLayout>(R.id.barra).childCount >= 6)

        // O que a tela remota desenha não pode ser só o fundo: tem de haver imagem variada no meio.
        val screen = activity.findViewById<RemoteView>(R.id.tela)
        val drawn = Apoio.desenha(screen)
        val colors = HashSet<Int>()
        for (x in 40 until drawn.width step 23) colors.add(drawn.getPixel(x, drawn.height / 2) and 0xF0F0F0)
        println("cores distintas na linha do meio da tela remota: ${colors.size}")
        assertTrue("a tela remota está de uma cor só", colors.size > 3)
        assertTrue(Color.alpha(drawn.getPixel(drawn.width / 2, drawn.height / 2)) == 255)
        Apoio.captura(activity, "$name.png")

        // A barra de teclas abre pelo botão "Teclas".
        val bar = activity.findViewById<LinearLayout>(R.id.barra)
        (0 until bar.childCount).map { bar.getChildAt(it) as TextView }.first { it.text == "Teclas" }.performClick()
        assertEquals(View.VISIBLE, activity.findViewById<View>(R.id.teclasRolagem).visibility)
        Apoio.espera(300) { false }
        Apoio.captura(activity, "$name-teclas.png")

        controller.pause().stop().destroy()
        // Dá tempo de o computador perceber o fim da sessão antes do próximo teste.
        Thread.sleep(1000)
    }

    private fun collect(view: View, into: MutableList<EditText>) {
        if (view is EditText) into.add(view)
        if (view is android.view.ViewGroup) for (i in 0 until view.childCount) collect(view.getChildAt(i), into)
    }
}
