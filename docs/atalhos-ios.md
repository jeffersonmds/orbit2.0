# Atalhos do iOS para o Orbit

Além do app na Tela de Início, você pode criar **Atalhos** (o app Atalhos da Apple) que aparecem
no menu **Compartilhar** de qualquer app: mandar um link para abrir no PC, copiar um texto para
o PC ou enviar fotos e arquivos. Também dá para chamar pela Siri, dizendo o nome do atalho.

Pré-requisito: a ponte já funcionando (veja `docs/tailscale-setup.md`) e o Tailscale conectado
no iPhone.

> Os nomes das ações abaixo estão como aparecem no iOS em português. Se a sua versão do iOS
> usar nomes ligeiramente diferentes, procure pelo equivalente na busca de ações do app Atalhos.

## 1. Um token só para os Atalhos

Use um token próprio para os Atalhos, separado do app. Assim você revoga um sem afetar o outro.

1. No PC: ilha > aba **Dispositivos** > **Parear dispositivo**. Aparece o QR e um link parecido com
   `https://notebook.tail1234.ts.net/#token=AbCdEf...`.
2. Copie o link (botão de copiar na aba) e envie para o iPhone (por exemplo, pelo próprio
   Orbit ou uma mensagem para você mesmo).
3. **Use só a parte depois de `#token=`**. No exemplo, `AbCdEf...` é o token. O endereço
   (`https://notebook.tail1234.ts.net`) é a base das chamadas.
4. Trate o token como uma senha: quem o tem controla o PC. Se vazar, revogue na aba Dispositivos (X).

Em todos os atalhos abaixo, a ação principal é **Obter Conteúdo do URL**, com o cabeçalho:

| Cabeçalho | Valor |
| --- | --- |
| `Authorization` | `Bearer SEU_TOKEN` |

(A palavra `Bearer`, um espaço e depois o token.)

Para adicionar o cabeçalho: dentro da ação, toque em **Mostrar Mais**, depois em
**Cabeçalhos** > **Adicionar novo cabeçalho**.

## 2. Atalho "Abrir no PC"

Recebe um link (por exemplo, de uma página no Safari) e abre no navegador do PC.

1. Abra o app **Atalhos**, toque em **+** para criar um novo atalho e dê o nome **Abrir no PC**.
2. Toque em **i** (detalhes) e ligue **Mostrar no Menu Compartilhar**. Em **Tipos aceitos**
   deixe só **URLs**.
3. Adicione a ação **Obter Conteúdo do URL** e configure:
   - **URL:** `https://notebook.tail1234.ts.net/api/links/open` (com o seu endereço).
   - Toque em **Mostrar Mais**.
   - **Método:** `POST`.
   - **Cabeçalhos:** `Authorization` = `Bearer SEU_TOKEN`.
   - **Corpo da Requisição:** `JSON`. Adicione um campo do tipo **Texto** com a chave `url` e,
     como valor, a variável **Entrada do Atalho** (toque no campo de valor e escolha
     "Entrada do Atalho" nas variáveis).
4. Pronto. No Safari, toque em Compartilhar, role e escolha **Abrir no PC**.

## 3. Atalho "Copiar para o PC"

Recebe um texto selecionado e coloca na área de transferência do PC.

1. Novo atalho chamado **Copiar para o PC**.
2. Detalhes (i): ligue **Mostrar no Menu Compartilhar** e deixe só **Texto** em Tipos aceitos.
3. Ação **Obter Conteúdo do URL**:
   - **URL:** `https://notebook.tail1234.ts.net/api/clipboard`
   - **Método:** `PUT`
   - **Cabeçalhos:** `Authorization` = `Bearer SEU_TOKEN`
   - **Corpo da Requisição:** `JSON`, campo **Texto** com a chave `text` e valor
     **Entrada do Atalho**.
4. Selecione um texto em qualquer app, Compartilhar > **Copiar para o PC**. É só colar no PC.

## 4. Atalho "Enviar para o PC"

Recebe fotos, vídeos e arquivos e salva na pasta `Downloads\Orbit` do PC.

1. Novo atalho chamado **Enviar para o PC**.
2. Detalhes (i): ligue **Mostrar no Menu Compartilhar** e marque em Tipos aceitos
   **Imagens**, **Arquivos** e **Mídia**.
3. Ação **Obter Conteúdo do URL**:
   - **URL:** `https://notebook.tail1234.ts.net/api/files`
   - **Método:** `POST`
   - **Cabeçalhos:** `Authorization` = `Bearer SEU_TOKEN`
   - **Corpo da Requisição:** `Formulário`. Adicione um campo do tipo **Arquivo** com o nome
     `file` e, como valor, a variável **Entrada do Atalho**.
4. Teste primeiro com 2 fotos, para conferir que o formulário está certo antes de mandar muitas.
5. Na galeria de fotos, selecione e escolha Compartilhar > **Enviar para o PC**.

Limites: até 500 MB por envio e até 50 arquivos por vez. Um arquivo com nome repetido ganha um
sufixo, nunca sobrescreve outro.

## Siri

O nome do atalho funciona como comando de voz. Por exemplo: "E aí Siri, Abrir no PC". Atalhos
que dependem de uma entrada (link, texto ou arquivo) pedem a entrada quando chamados pela Siri.

## Referência das rotas

Todas exigem `Authorization: Bearer <token>`, exceto `/health`. O endereço base é o do seu
Tailscale (por exemplo, `https://notebook.tail1234.ts.net`).

| Método e rota | O que faz | Corpo |
| --- | --- | --- |
| `GET /health` | Confere se a ponte está de pé (sem token) | - |
| `GET /api/stats` | CPU, RAM, disco e bateria | - |
| `POST /api/links/open` | Abre um link no navegador do PC (só `http` ou `https`) | JSON `{ "url": "https://..." }` |
| `GET /api/clipboard` | Lê o texto da área de transferência do PC | - |
| `PUT /api/clipboard` | Grava texto na área de transferência do PC | JSON `{ "text": "..." }` |
| `POST /api/power/{acao}` | `lock`, `sleep`, `restart` ou `shutdown` | - |
| `GET /api/media` | Música tocando e volume | - |
| `POST /api/media/{comando}` | `playpause`, `next` ou `prev` | - |
| `PUT /api/media/volume` | Ajusta o volume | JSON `{ "percent": 0-100 }` |
| `POST /api/files` | Envia arquivos para o PC | Formulário (`multipart/form-data`), campo `file` |

Corpos JSON são limitados a 1 MB; só os envios de `/api/files` aceitam até 500 MB. O texto do
clipboard tem limite de 100.000 caracteres.

## Códigos de resposta

| Código | Significado |
| --- | --- |
| `200` | Deu certo e a resposta tem conteúdo (ler texto, mídia, stats, enviar arquivos) |
| `204` | Deu certo, sem conteúdo (abrir link, gravar texto, mídia, energia) |
| `413` | Corpo acima do limite (1 MB no JSON; 500 MB em `/api/files`) |
| `400` | Entrada inválida: link que não é http/https, corpo errado, nenhum arquivo enviado |
| `401` | Token ausente, errado ou revogado |
| `404` | Ação inexistente (por exemplo, `/api/power/abc`) |
| `429` | Rápido demais (limite de 120 ações por minuto, **compartilhado** entre todos os aparelhos e o app aberto); espere um pouco |
| `500` | Falha no PC ao executar a ação; veja o log do Orbit |

No app Atalhos, uma resposta de erro (por exemplo, 401) não interrompe o atalho por si só: se
nada acontecer, adicione uma ação **Mostrar Resultado** depois da chamada para ver o que o PC
respondeu.
