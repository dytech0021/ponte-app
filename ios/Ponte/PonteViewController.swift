import AuthenticationServices
import UIKit
import WebKit

/// A página do Ponte (https://filabox3d.com.br/ponte/) ocupando o app, com o que ela não consegue fazer sozinha aqui
/// dentro: o login com Google (o Google recusa páginas dentro de apps), tela acesa e tela cheia durante a sessão
/// (a página avisa pelo canal "ponte") e links de fora abrindo no Safari.
final class PonteViewController: UIViewController, WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler {
    static let site = URL(string: "https://filabox3d.com.br/ponte/")!
    /// O Supabase do FilaBox só devolve o login a endereços do site (ponte:// ele recusa): esta página do site passa o
    /// código adiante para ponte://conta, que volta para cá.
    static let googleReturn = "https://filabox3d.com.br/ponte/ios-retorno.html"

    private var web: WKWebView!
    private let offline = UIStackView()
    private var googleLogin: ASWebAuthenticationSession?
    private var loginAnchor: LoginAnchor?   // a sessão de login só guarda uma referência fraca
    private var fullScreen = false {
        didSet {
            setNeedsStatusBarAppearanceUpdate()
            setNeedsUpdateOfHomeIndicatorAutoHidden()
        }
    }

    override func loadView() {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .default()
        configuration.allowsInlineMediaPlayback = true
        configuration.mediaTypesRequiringUserActionForPlayback = []
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
        configuration.applicationNameForUserAgent = "Mobile/15E148 PonteApp-iOS/\(version)"
        configuration.userContentController.add(self, name: "ponte")

        web = WKWebView(frame: .zero, configuration: configuration)
        web.navigationDelegate = self
        web.uiDelegate = self
        web.allowsBackForwardNavigationGestures = false
        web.allowsLinkPreview = false
        web.isOpaque = false
        web.backgroundColor = .clear
        web.scrollView.backgroundColor = .clear
        web.scrollView.bounces = false
        web.scrollView.contentInsetAdjustmentBehavior = .never

        let root = UIView()
        // Até a página dizer o fundo dela (mensagem "fundo"): o --fundo de web/app.css, claro ou escuro como o aparelho.
        root.backgroundColor = UIColor { traits in
            traits.userInterfaceStyle == .dark
                ? UIColor(red: 0x12 / 255, green: 0x1a / 255, blue: 0x1d / 255, alpha: 1)
                : UIColor(red: 0xf3 / 255, green: 0xf5 / 255, blue: 0xf6 / 255, alpha: 1)
        }
        root.addSubview(web)
        // Como a página adicionada à Tela de Início: começa abaixo da barra de estado e vai até as bordas, onde o CSS
        // já respeita as áreas seguras (env(safe-area-inset-*)) do entalhe e da barra de início.
        web.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            web.topAnchor.constraint(equalTo: root.safeAreaLayoutGuide.topAnchor),
            web.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            web.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            web.bottomAnchor.constraint(equalTo: root.bottomAnchor),
        ])

        let text = UILabel()
        text.text = "Sem conexão com o Ponte.\nConfira a internet e tente de novo."
        text.numberOfLines = 0
        text.textAlignment = .center
        text.font = .preferredFont(forTextStyle: .body)
        let retry = UIButton(type: .system)
        retry.setTitle("Tentar de novo", for: .normal)
        retry.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        retry.addAction(UIAction { [weak self] _ in self?.loadHome() }, for: .touchUpInside)
        offline.axis = .vertical
        offline.alignment = .center
        offline.spacing = 16
        offline.addArrangedSubview(text)
        offline.addArrangedSubview(retry)
        offline.isHidden = true
        offline.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(offline)
        NSLayoutConstraint.activate([
            offline.centerYAnchor.constraint(equalTo: root.centerYAnchor),
            offline.leadingAnchor.constraint(equalTo: root.layoutMarginsGuide.leadingAnchor),
            offline.trailingAnchor.constraint(equalTo: root.layoutMarginsGuide.trailingAnchor),
        ])
        view = root
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        loadHome()
    }

    override var preferredStatusBarStyle: UIStatusBarStyle {
        var white: CGFloat = 1
        _ = view.backgroundColor?.resolvedColor(with: traitCollection).getWhite(&white, alpha: nil)
        return white < 0.5 ? .lightContent : .darkContent
    }

    override var prefersStatusBarHidden: Bool { fullScreen }
    override var prefersHomeIndicatorAutoHidden: Bool { fullScreen }

    private func loadHome() {
        offline.isHidden = true
        web.isHidden = false
        web.load(URLRequest(url: Self.site))
    }

    /// ponte://conta?code=... (ou ?error=...) é a volta do login com Google: a página termina o login com o segredo do
    /// PKCE que ela guardou antes de sair, como faz no navegador.
    func open(_ url: URL) {
        guard url.scheme == "ponte", url.host == "conta",
              let back = URLComponents(url: url, resolvingAgainstBaseURL: false),
              var page = URLComponents(url: Self.site, resolvingAgainstBaseURL: false) else { return }
        page.percentEncodedQuery = back.percentEncodedQuery
        page.percentEncodedFragment = back.percentEncodedFragment
        guard let address = page.url else { return }
        loadViewIfNeeded()
        offline.isHidden = true
        web.isHidden = false
        web.load(URLRequest(url: address))
    }

    // MARK: - login com Google

    /// A página manda para /auth/v1/authorize do Supabase; aqui isso abre na tela de login do sistema, que o Google
    /// aceita, com a volta trocada para a página do site que passa o código ao app.
    private func startGoogle(_ url: URL) {
        guard var authorize = URLComponents(url: url, resolvingAgainstBaseURL: false), let window = view.window else { return }
        var items = authorize.queryItems ?? []
        items.removeAll { $0.name == "redirect_to" }
        items.append(URLQueryItem(name: "redirect_to", value: Self.googleReturn))
        authorize.queryItems = items
        guard let address = authorize.url else { return }
        googleLogin?.cancel()
        let login = ASWebAuthenticationSession(url: address, callbackURLScheme: "ponte") { [weak self] back, error in
            DispatchQueue.main.async {
                guard let self else { return }
                self.googleLogin = nil
                if let back {
                    self.open(back)
                } else if let error, (error as? ASWebAuthenticationSessionError)?.code != .canceledLogin {
                    // A página mostra o motivo, como mostra os erros que o Google devolve.
                    var page = URLComponents(url: Self.site, resolvingAgainstBaseURL: false)!
                    page.queryItems = [URLQueryItem(name: "error_description", value: error.localizedDescription)]
                    if let address = page.url { self.web.load(URLRequest(url: address)) }
                }
            }
        }
        loginAnchor = LoginAnchor(window: window)
        login.presentationContextProvider = loginAnchor
        googleLogin = login
        login.start()
    }

    // MARK: - WKNavigationDelegate

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = navigationAction.request.url else { return decisionHandler(.cancel) }
        if url.path.hasSuffix("/auth/v1/authorize") {
            decisionHandler(.cancel)
            startGoogle(url)
        } else if Self.isPonte(url) || ["about", "blob", "data"].contains(url.scheme ?? "")
                    || navigationAction.targetFrame?.isMainFrame == false {
            decisionHandler(.allow)
        } else {
            // O que não é do Ponte (o site do FilaBox, o Exilados) abre no Safari.
            decisionHandler(.cancel)
            UIApplication.shared.open(url)
        }
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        offline.isHidden = true
        web.isHidden = false
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        failed(error)
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        failed(error)
    }

    /// O iOS encerra o processo da página quando falta memória com o app em segundo plano: ela abre de novo.
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        loadHome()
    }

    private func failed(_ error: Error) {
        // Navegação cancelada aqui (o login com Google, links de fora) não é falta de internet.
        let error = error as NSError
        guard error.domain == NSURLErrorDomain, error.code != NSURLErrorCancelled else { return }
        web.isHidden = true
        offline.isHidden = false
    }

    // MARK: - WKUIDelegate

    /// window.open (Exilados, cadastro e senha do FilaBox): no Safari.
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = navigationAction.request.url { UIApplication.shared.open(url) }
        return nil
    }

    // MARK: - WKScriptMessageHandler

    /// window.webkit.messageHandlers.ponte.postMessage({ sessao, telaCheia, fundo }) em app.js.
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.frameInfo.securityOrigin.host == Self.site.host, let body = message.body as? [String: Any] else { return }
        if let session = body["sessao"] as? Bool { UIApplication.shared.isIdleTimerDisabled = session }
        if let on = body["telaCheia"] as? Bool { fullScreen = on }
        if let css = body["fundo"] as? String, let color = Self.color(css: css) {
            view.backgroundColor = color
            setNeedsStatusBarAppearanceUpdate()
        }
    }

    private static func isPonte(_ url: URL) -> Bool {
        url.scheme == "https" && url.host == site.host && url.path.hasPrefix("/ponte")
    }

    /// "rgb(243, 245, 246)" ou "rgba(...)", como o getComputedStyle devolve; transparente não serve de fundo.
    private static func color(css: String) -> UIColor? {
        let numbers = css.split(whereSeparator: { !"0123456789.".contains($0) }).compactMap { Double($0) }
        guard numbers.count >= 3 else { return nil }
        let alpha = numbers.count >= 4 ? numbers[3] : 1
        guard alpha > 0 else { return nil }
        return UIColor(red: numbers[0] / 255, green: numbers[1] / 255, blue: numbers[2] / 255, alpha: alpha)
    }
}

/// A janela sobre a qual o login com Google aparece (fora do controlador para não depender do ator principal).
private final class LoginAnchor: NSObject, ASWebAuthenticationPresentationContextProviding {
    let window: UIWindow

    init(window: UIWindow) {
        self.window = window
    }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        window
    }
}
