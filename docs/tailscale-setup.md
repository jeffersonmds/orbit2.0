# Ligando iPhone e iPad ao Orbit com Tailscale

O Orbit tem uma "ponte" que deixa o seu iPhone ou iPad abrir links no PC, trocar texto, enviar arquivos,
controlar a música e ver o estado do computador. Para o iPhone alcançar o PC de qualquer lugar
sem abrir nada para a internet, usamos o **Tailscale**: uma rede privada só sua, que liga os seus
aparelhos entre si.

Como funciona, em uma frase: o Orbit escuta apenas dentro do próprio PC (`127.0.0.1`, porta
5191) e o Tailscale cria uma "porta" HTTPS privada, visível só para os seus aparelhos, que
encaminha para ele. Mesmo assim, toda ação exige um **token** (uma senha longa gerada no pareamento).

> Importante: nunca use `tailscale funnel`. Ele publica o PC na internet aberta. Aqui usamos
> sempre `tailscale serve`, que só vale dentro da sua rede privada.

## Antes de começar

- O Orbit 2.0 precisa estar rodando no PC.
- Você precisa de uma conta gratuita do Tailscale (o plano pessoal basta).

## Passo a passo

### 1. Instalar o Tailscale nos dois aparelhos

1. No Windows: baixe em tailscale.com/download e instale.
2. No iPhone: instale o app **Tailscale** pela App Store.
3. Entre nos dois com a **mesma conta**.
4. Ative a **verificação em duas etapas (2FA)** na conta usada para entrar (Google, Microsoft,
   Apple etc.). Quem entra na sua conta entra na sua rede; o 2FA é a primeira proteção.

### 2. Ligar MagicDNS e certificados HTTPS

1. Abra login.tailscale.com e entre.
2. Vá em **DNS**.
3. Ligue **MagicDNS** (dá um nome fácil ao PC, em vez de um número).
4. Em **HTTPS Certificates**, clique em ativar. É isso que permite o endereço `https://`, que o
   iPhone exige para instalar o app na Tela de Início.

### 3. Anotar o nome do PC

Em login.tailscale.com, aba **Machines**, veja o nome completo do seu PC. Ele tem este formato:

```
notebook.tail1234.ts.net
```

(`notebook` é o nome do PC e `tail1234.ts.net` é o nome da sua rede; o seu será diferente.)
O sufixo `tailXXXX.ts.net` aparece na página **DNS** do painel ou no detalhe da máquina.
Ainda na aba Machines, anote o **IP 100.x.y.z** do PC: ele é usado na ACL do passo 7.
Anote esse nome: ele é usado nos próximos passos. Nos exemplos abaixo, troque pelo seu.

### 4. Ligar a ponte no Orbit

1. Abra a ilha e vá na aba **Dispositivos**.
2. Ligue **Ponte ligada** (porta 5191).
3. No campo **Endereço do Tailscale**, escreva `https://notebook.tail1234.ts.net` (com o seu nome).

A ponte vem **desligada por padrão**. Se quiser parar de aceitar o iPhone, é só desligá-la aqui.

### 5. Criar a porta privada com `tailscale serve`

Abra o **PowerShell** no PC e rode:

```powershell
tailscale serve --bg --https=443 http://127.0.0.1:5191
```

Confira se ficou ativo:

```powershell
tailscale serve status
```

Deve aparecer o seu endereço `https://notebook.tail1234.ts.net` apontando para
`http://127.0.0.1:5191`. O `--bg` deixa isso funcionando em segundo plano, inclusive depois de
reiniciar o PC.

Para desfazer depois, use `tailscale serve --https=443 off` (desliga só essa porta) ou
`tailscale serve reset` (limpa tudo). A sintaxe exata pode mudar entre versões do Tailscale:
confira com `tailscale serve --help`. Documentação oficial: tailscale.com/kb/1312/serve.

> Se o comando acima der erro de sintaxe na sua versão, não é o Orbit: o Tailscale mudou os
> parâmetros. Veja `tailscale serve --help` e a página oficial acima, e mantenha a mesma ideia:
> HTTPS na porta 443 encaminhando para `http://127.0.0.1:5191`.

### 6. Parear o iPhone

1. Com o Tailscale **conectado** no iPhone, volte à ilha: aba **Dispositivos** > **Parear dispositivo**.
2. Aponte a câmera do iPhone para o QR que aparece.
3. Toque no aviso para abrir. O Safari abre o painel do Orbit já com o token guardado.
4. Toque em **Compartilhar** (o quadrado com a seta) > **Adicionar à Tela de Início**.

**Atenção:** no iPhone, o app adicionado à Tela de Início tem armazenamento próprio e **não
herda** o token que o Safari guardou. Na primeira vez, o app instalado abre pedindo o token. É
só uma vez:

1. Abra o Orbit pelo **ícone da Tela de Início**.
2. Aponte a câmera para o QR (na aba **Dispositivos** do PC > **Parear dispositivo**).
3. **Toque e segure** o aviso amarelo que aparece e toque em **Copiar** (não em abrir).
4. Volte ao app, cole no campo (ele aceita o link inteiro ou só o token) e toque em **Salvar**.

Sem o QR à mão, use o botão **Copiar** da aba Dispositivos no PC e mande o link para o iPhone por
qualquer meio seguro (por exemplo, uma mensagem para você mesmo). O token fica salvo só nesse
aparelho.

### 7. Segurança extra (recomendado)

**a) Restringir quem alcança o PC (ACL).** Por padrão, todos os seus aparelhos enxergam uns aos
outros. Você pode limitar para que só os seus aparelhos (todos os logados na sua conta) falem
com a porta 443 do PC. No painel, abra
**Access controls** (aba da política) e use algo assim, trocando e-mail e nomes pelos seus:

```json
{
  "hosts": { "notebook": "100.x.y.z" },
  "acls": [
    {
      "action": "accept",
      "src": ["voce@exemplo.com"],
      "dst": ["notebook:443"]
    }
  ]
}
```

O nome `notebook` em `dst` só vale porque foi definido em `"hosts"`: troque `100.x.y.z` pelo IP
do seu PC (aba Machines). Isso diz: o usuário `voce@exemplo.com` (ou seja, os aparelhos da sua conta) pode acessar o
aparelho `notebook` somente na porta `443`. Se quiser limitar a um único aparelho, pode usar
tags ou o nome do iPhone no lugar do e-mail; a lista completa de formatos aceitos está em
tailscale.com/kb/1018/acls. Cuidado: ao salvar uma política nova, tudo que não estiver
liberado nela deixa de funcionar. O editor tem uma área de **testes** (Tests) para você validar
antes de salvar.

Para conferir se a regra funciona, no PowerShell do PC rode:

```powershell
tailscale status
```

Isso lista os aparelhos que o PC enxerga. Depois teste pelo iPhone abrindo o endereço do Orbit
no Safari; se carregar o painel, o acesso à porta 443 está liberado. Para ver o que **não**
deve funcionar, tente de outro aparelho que a regra não inclui.

**b) Tailnet Lock.** Impede que um aparelho novo entre na sua rede sem a assinatura de um
aparelho que você já confia, mesmo que alguém invada a conta. É opcional e um pouco mais
avançado: siga a documentação oficial (tailscale.com/kb/1226/tailnet-lock) e o comando
`tailscale lock init` descrito lá. Leia tudo antes de ativar, porque configurar errado pode
atrapalhar a entrada de aparelhos novos.

### 8. Revogar um aparelho (perdeu ou trocou o iPhone)

Na ilha: aba **Dispositivos** > na lista de aparelhos pareados, clique no **X** ao lado do token.
Esse aparelho perde o acesso na hora (o token deixa de valer). Se quiser pareá-lo de novo,
use **Parear dispositivo** outra vez. Para cortar de vez o aparelho da rede, remova-o também no
painel do Tailscale (aba Machines).

### 9. Problemas comuns

| Sintoma | O que verificar |
| --- | --- |
| Painel mostra **PC fora do ar** | O PC está dormindo ou desligado; ou a **Ponte ligada** está desligada na ilha; ou o Orbit não está aberto; ou o `tailscale serve` parou (rode `tailscale serve status`). Confirme também que o Tailscale está conectado no iPhone. |
| **Token inválido** / pede para parear | O token foi revogado ou você reinstalou o app. Faça **Parear dispositivo** de novo. |
| A página nem abre no Safari | O Tailscale do iPhone está desligado, ou o endereço está errado, ou o HTTPS Certificates não foi ativado (passo 2). |
| **Calma: muitas ações seguidas** | O limite de segurança é de 120 ações por minuto. Espere um instante. |
| Arquivos enviados não aparecem | Eles vão para `Downloads\Orbit` (ou a pasta configurada na aba Dispositivos). |

Todas as ações feitas pelo iPhone (abrir link, energia, arquivos etc.) ficam registradas em
`%AppData%\Orbit2\audit.jsonl`, caso queira conferir o que aconteceu.

## Vários aparelhos (iPhone e iPad)

Cada aparelho é pareado separadamente (passo 6) e tem as próprias permissões na aba **Dispositivos**:
Câmera, Arquivos, Tela, Controle, Microfone e Teclado são por aparelho, desligados por padrão. A permissão
vale também no momento do uso: desligar derruba o que o aparelho estiver fazendo. A caixa de saída ("Recebidos
do PC") é por destinatário; com mais de um aparelho pareado o Orbit pergunta para qual mandar. O PWA usa a
tela larga do iPad (grade de 2 a 4 colunas).
