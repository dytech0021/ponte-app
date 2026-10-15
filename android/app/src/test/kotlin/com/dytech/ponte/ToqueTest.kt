package com.dytech.ponte

import android.graphics.Color
import android.os.SystemClock
import android.view.MotionEvent
import android.view.View
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import ponte.core.MouseAction

/**
 * Os gestos da tela remota viram as mensagens de mouse certas. A tela do teste tem 1080 x 1920 e mostra
 * um computador de 1920 x 1080: a imagem ocupa a largura toda, com escala 0,5625, centralizada na altura.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], qualifiers = "w360dp-h640dp-xxhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class ToqueTest {
    private lateinit var view: RemoteView
    private val recorder = Recorder()

    @Before
    fun prepara() {
        // A tela precisa estar numa janela para os atrasos (como o de "segurar") funcionarem.
        val activity = Robolectric.buildActivity(android.app.Activity::class.java).setup().get()
        view = RemoteView(activity)
        activity.setContentView(view)
        view.output = recorder
        view.measure(View.MeasureSpec.makeMeasureSpec(1080, View.MeasureSpec.EXACTLY), View.MeasureSpec.makeMeasureSpec(1920, View.MeasureSpec.EXACTLY))
        view.layout(0, 0, 1080, 1920)
        view.applyFrame(Apoio.quadro(1920, 1080, Color.rgb(20, 120, 200)))
        Apoio.desenha(view)
        Apoio.avanca(1000)
    }

    private fun down(x: Float, y: Float): Long {
        val time = SystemClock.uptimeMillis()
        Apoio.toque(view, MotionEvent.ACTION_DOWN, x, y, time)
        return time
    }

    private fun move(x: Float, y: Float, downTime: Long) {
        Apoio.avanca(20)
        Apoio.toque(view, MotionEvent.ACTION_MOVE, x, y, downTime)
    }

    private fun up(x: Float, y: Float, downTime: Long) {
        Apoio.avanca(20)
        Apoio.toque(view, MotionEvent.ACTION_UP, x, y, downTime)
    }

    private fun tap(x: Float, y: Float) = up(x, y, down(x, y))

    // Confere uma mensagem de mouse: a ação e o ponto do computador, com folga de um pixel e meio de lá.
    private fun assertMouse(event: String, action: Int, remoteX: Float, remoteY: Float) {
        val parts = event.split(' ')
        assertEquals("mouse", parts[0])
        assertEquals(action, parts[1].toInt())
        assertEquals("x em $event", remoteX, parts[2].toInt() * 1919f / 65535f, 1.5f)
        assertEquals("y em $event", remoteY, parts[3].toInt() * 1079f / 65535f, 1.5f)
    }

    private fun assertClick(events: List<String>, down: Int, up: Int, remoteX: Float, remoteY: Float) {
        assertEquals(3, events.size)
        assertMouse(events[0], MouseAction.MOVE, remoteX, remoteY)
        assertMouse(events[1], down, remoteX, remoteY)
        assertMouse(events[2], up, remoteX, remoteY)
    }

    @Test
    fun telaCheiaCobreOAparelhoEOToqueContinuaCerto() {
        view.fill = true
        // A imagem cobre a altura inteira (escala 1920/1080 = 1,7778): no alto já não há faixa escura.
        assertTrue(Color.blue(Apoio.desenha(view).getPixel(540, 100)) > 150)
        // Da largura de 1920 pontos, o aparelho mostra 607,5, centralizados: a borda esquerda é o ponto 656,25.
        tap(0f, 960f)
        assertClick(recorder.take(), MouseAction.LEFT_DOWN, MouseAction.LEFT_UP, 656.25f, 540f)
    }

    @Test
    fun noToqueOPonteiroApareceOndeOComputadorDiz() {
        // O ponto (550, 992) cai dentro da seta desenhada com a ponta no meio da tela.
        assertTrue("sem posição nem toque, não há ponteiro", Color.red(Apoio.desenha(view).getPixel(550, 992)) < 80)
        view.setPointerPosition(32768, 32768, true)
        Apoio.avanca(20)
        val ponto = Apoio.desenha(view).getPixel(550, 992)
        assertTrue("o ponteiro deveria aparecer no meio da tela, veio ${Integer.toHexString(ponto)}", Color.red(ponto) > 200 && Color.green(ponto) > 200)
        // Em outro monitor do computador, o ponteiro some daqui.
        view.setPointerPosition(0, 0, false)
        Apoio.avanca(20)
        assertTrue(Color.red(Apoio.desenha(view).getPixel(550, 992)) < 80)
    }

    @Test
    fun aImagemApareceNaTela() {
        val drawn = Apoio.desenha(view)
        // No meio da tela está a imagem do computador; no alto, a faixa escura que sobra.
        val center = drawn.getPixel(540, 960)
        assertTrue("o centro deveria ser azul, veio ${Integer.toHexString(center)}", Color.blue(center) > 150 && Color.red(center) < 80)
        assertEquals(0, Color.blue(drawn.getPixel(540, 100)))
    }

    @Test
    fun toqueClicaOndeODedoEncostou() {
        tap(540f, 960f)
        // O meio da tela do aparelho é o meio da tela do computador: (960, 540).
        assertClick(recorder.take(), MouseAction.LEFT_DOWN, MouseAction.LEFT_UP, 960f, 540f)
        // O canto superior esquerdo da imagem fica em y = 656 na tela do aparelho.
        tap(0f, 657f)
        assertClick(recorder.take(), MouseAction.LEFT_DOWN, MouseAction.LEFT_UP, 0f, 1.3f)
        // Tocar na faixa escura acima da imagem acerta a borda dela, nunca fora da tela do computador.
        tap(540f, 100f)
        assertClick(recorder.take(), MouseAction.LEFT_DOWN, MouseAction.LEFT_UP, 960f, 0f)
    }

    @Test
    fun segurarESoltarEoBotaoDireito() {
        val time = down(540f, 960f)
        Apoio.avanca(700)
        // Segurar só "pega" o ponto: o ponteiro de lá vai para ele, mas nada é clicado ainda.
        assertEquals(listOf(MouseAction.MOVE), recorder.actions())
        recorder.take()
        up(540f, 960f, time)
        assertClick(recorder.take(), MouseAction.RIGHT_DOWN, MouseAction.RIGHT_UP, 960f, 540f)
    }

    @Test
    fun segurarEArrastarLevaAJanelaJunto() {
        // Segura na "barra de título" (ponto 960,540 do computador) até vibrar e arrasta 100 px para a direita.
        val time = down(540f, 960f)
        Apoio.avanca(700)
        recorder.take()
        move(570f, 960f, time)
        move(600f, 960f, time)
        move(640f, 960f, time)
        up(640f, 960f, time)
        val events = recorder.take()
        // O botão esquerdo desce no ponto pego, o ponteiro acompanha o dedo e o botão sobe onde o dedo parou.
        assertMouse(events[0], MouseAction.MOVE, 960f, 540f)
        assertMouse(events[1], MouseAction.LEFT_DOWN, 960f, 540f)
        assertMouse(events.last(), MouseAction.LEFT_UP, 640f / 0.5625f, 540f)
        assertTrue("faltaram movimentos com o botão apertado", events.count { it.startsWith("mouse ${MouseAction.MOVE} ") } >= 3)
        assertTrue("arrastar não pode virar botão direito", events.none { it.startsWith("mouse ${MouseAction.RIGHT_DOWN} ") })
    }

    @Test
    fun noTouchpadSegurarEArrastarLevaAJanelaPeloPonteiro() {
        view.touchpad = true
        // O ponteiro começa no meio da tela do computador; o dedo encosta longe dele.
        val time = down(300f, 1500f)
        Apoio.avanca(700)
        recorder.take()
        move(350f, 1500f, time)
        move(400f, 1500f, time)
        up(400f, 1500f, time)
        val events = recorder.take()
        // O botão desce onde o ponteiro estava, e não onde o dedo encostou; depois o ponteiro anda com o dedo.
        assertMouse(events[1], MouseAction.LEFT_DOWN, 960f, 540f)
        assertMouse(events.last(), MouseAction.LEFT_UP, 960f + 100f / 0.5625f * 1.3f, 540f)
    }

    @Test
    fun doisToquesCaemNoMesmoPonto() {
        tap(540f, 960f)
        Apoio.avanca(80)
        // O segundo toque cai alguns pixels ao lado, como acontece com o dedo; o clique sai no ponto do primeiro.
        tap(552f, 951f)
        val events = recorder.take()
        assertEquals(6, events.size)
        assertEquals(events[0].substringAfter("mouse 0 "), events[3].substringAfter("mouse 0 "))
    }

    @Test
    fun tocarDuasVezesEArrastarSeguraOBotao() {
        tap(540f, 960f)
        recorder.take()
        Apoio.avanca(80)
        val time = down(540f, 960f)
        move(640f, 960f, time)
        move(740f, 1000f, time)
        up(740f, 1000f, time)
        val actions = recorder.actions()
        assertEquals(MouseAction.MOVE, actions[0])
        assertEquals(MouseAction.LEFT_DOWN, actions[1])
        assertEquals(MouseAction.LEFT_UP, actions.last())
        assertTrue("faltaram movimentos durante o arraste", actions.count { it == MouseAction.MOVE } >= 3)
        // O botão não desce no ponto do primeiro toque (960,540), senão o Windows entende clique duplo e
        // maximiza a janela: desce 10 px adiante, na direção em que o dedo começou a andar (para a direita).
        assertMouse(recorder.events[1], MouseAction.LEFT_DOWN, 970f, 540f)
        // O botão é solto onde o dedo parou: 740 / 0,5625 = 1316 e (1000 - 656,25) / 0,5625 = 611.
        assertMouse(recorder.events.last(), MouseAction.LEFT_UP, 1315.6f, 611.1f)
    }

    @Test
    fun arrastarComUmDedoNaoMexeNoMouse() {
        val time = down(540f, 960f)
        move(640f, 1000f, time)
        move(700f, 1100f, time)
        up(700f, 1100f, time)
        assertEquals("arrastar só move a imagem ampliada", emptyList<String>(), recorder.take())
    }

    @Test
    fun doisDedosRolamAPagina() {
        val time = down(400f, 900f)
        Apoio.avanca(20)
        Apoio.doisDedos(view, MotionEvent.ACTION_POINTER_DOWN, 400f, 900f, 600f, 900f, time)
        for (step in 1..8) {
            Apoio.avanca(20)
            Apoio.doisDedos(view, MotionEvent.ACTION_MOVE, 400f, 900f + step * 60f, 600f, 900f + step * 60f, time)
        }
        Apoio.avanca(20)
        Apoio.doisDedos(view, MotionEvent.ACTION_POINTER_UP, 400f, 1380f, 600f, 1380f, time)
        up(400f, 1380f, time)
        val wheel = recorder.take().filter { it.startsWith("mouse ${MouseAction.WHEEL} ") }
        assertTrue("deveria ter rolado: $wheel", wheel.isNotEmpty())
        // Dedos para baixo trazem o conteúdo junto, como na tela do celular: roda para cima, valor positivo.
        assertTrue(wheel.all { it.endsWith("roda 120") })
    }

    @Test
    fun doisDedosParadosSaoOBotaoDireito() {
        val time = down(400f, 900f)
        Apoio.avanca(20)
        Apoio.doisDedos(view, MotionEvent.ACTION_POINTER_DOWN, 400f, 900f, 600f, 900f, time)
        Apoio.avanca(40)
        Apoio.doisDedos(view, MotionEvent.ACTION_POINTER_UP, 400f, 900f, 600f, 900f, time)
        up(400f, 900f, time)
        assertEquals(listOf(MouseAction.MOVE, MouseAction.RIGHT_DOWN, MouseAction.RIGHT_UP), recorder.actions())
    }

    @Test
    fun pincaAmpliaEOToquePassaAAcertarOutroPonto() {
        tap(200f, 800f)
        val before = recorder.take().first()
        val time = down(440f, 960f)
        Apoio.avanca(20)
        Apoio.doisDedos(view, MotionEvent.ACTION_POINTER_DOWN, 440f, 960f, 640f, 960f, time)
        for (step in 1..6) {
            Apoio.avanca(20)
            Apoio.doisDedos(view, MotionEvent.ACTION_MOVE, 440f - step * 40f, 960f, 640f + step * 40f, 960f, time)
        }
        Apoio.avanca(20)
        Apoio.doisDedos(view, MotionEvent.ACTION_POINTER_UP, 200f, 960f, 880f, 960f, time)
        up(200f, 960f, time)
        assertEquals("a pinça não manda nada ao computador", emptyList<String>(), recorder.take())
        Apoio.avanca(600)
        tap(200f, 800f)
        assertNotEquals("com a imagem ampliada, o mesmo ponto da tela é outro ponto do computador", before, recorder.take().first())
    }

    @Test
    fun touchpadMoveOPonteiroEClicaOndeEleEsta() {
        view.touchpad = true
        // O ponteiro começa no meio da tela do computador. Arrastar 100 px na tela move 100 / 0,5625 x 1,3 = 231 px lá.
        val time = down(300f, 1500f)
        move(350f, 1500f, time)
        move(400f, 1500f, time)
        up(400f, 1500f, time)
        val moves = recorder.take()
        assertTrue(moves.isNotEmpty() && moves.all { it.startsWith("mouse 0 ") })
        assertMouse(moves.last(), MouseAction.MOVE, 1191.1f, 540f)
        // O toque clica onde o ponteiro ficou, e não onde o dedo encostou.
        tap(900f, 300f)
        assertClick(recorder.take(), MouseAction.LEFT_DOWN, MouseAction.LEFT_UP, 1191.1f, 540f)
    }

    @Test
    fun cliqueComCtrlArmadoSeguraATecla() {
        val keyboard = Keyboard { recorder }
        view.keyboard = keyboard
        keyboard.toggleArmed(Vk.CONTROL)
        tap(540f, 960f)
        val events = recorder.take()
        assertEquals("tecla 0x11 aperta", events.first())
        assertEquals("tecla 0x11 solta", events.last())
        assertEquals(5, events.size)
        assertTrue("o Ctrl vale para um clique só", !keyboard.isArmed(Vk.CONTROL))
    }
}
