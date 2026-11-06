'use strict';
// Também usado pelo harness: IDs WinRT/WASAPI não são IDs do Chromium.
function rtcDevice(devices, kind, id, label) {
  const list = devices.filter(d => d.kind === kind && d.deviceId !== 'default' && d.deviceId !== 'communications');
  const exact = list.find(d => d.deviceId === id);
  if (exact) return exact.deviceId;
  const normalize = s => s.trim().toLowerCase().replace(/\s+/g, ' ').replace(/\s+\([0-9a-f]{4}:[0-9a-f]{4}\)$/, '');
  const matches = list.filter(d => normalize(d.label) === normalize(label));
  return label.trim() && matches.length === 1 ? matches[0].deviceId : null;
}
let pc = null, stream = null, stopped = false, started = false;
let offered = false, pendingIce = [];
const send = data => window.chrome.webview.postMessage(data);
function stop() {
  stopped = true;
  stream?.getTracks().forEach(t => t.stop()); stream = null;
  if (pc) { pc.onconnectionstatechange = null; pc.close(); pc = null; }
}
async function receive(m) {
  if (m.t === 'start' && !started) {
    started = true;
    const video = m.kind === 'camera';
    const id = rtcDevice(await navigator.mediaDevices.enumerateDevices(), video ? 'videoinput' : 'audioinput', m.deviceId, m.label);
    if (!id) throw new Error('Dispositivo não identificado');
    const captured = await navigator.mediaDevices.getUserMedia(video
      ? { video: { deviceId: { exact: id }, width: { ideal: m.width }, frameRate: { ideal: m.fps, max: m.fps } }, audio: false }
      : { video: false, audio: { deviceId: { exact: id }, echoCancellation: false, noiseSuppression: false, autoGainControl: false } });
    if (stopped) { captured.getTracks().forEach(t => t.stop()); return; }
    stream = captured; pc = new RTCPeerConnection({ iceServers: [] });
    pc.onicecandidate = e => {
      if (!e.candidate?.candidate) return;
      const message = { t: 'ice', candidate: e.candidate.toJSON() };
      if (offered) send(message); else pendingIce.push(message);
    };
    pc.onconnectionstatechange = () => { if (pc && pc.connectionState === 'failed') { send({ t: 'error' }); stop(); } };
    for (const track of stream.getTracks()) {
      track.onended = () => { send({ t: 'error' }); stop(); };
      const transceiver = pc.addTransceiver(track, { direction: 'sendonly', streams: [stream] });
      const codecs = RTCRtpSender.getCapabilities(track.kind)?.codecs;
      if (codecs && transceiver.setCodecPreferences) {
        const preferred = codecs.filter(c => /^(video\/(H264|VP8)|audio\/opus)$/i.test(c.mimeType));
        if (preferred.length) transceiver.setCodecPreferences(preferred);
      }
    }
    await pc.setLocalDescription(await pc.createOffer());
    if (stopped) return;
    send({ t: 'offer', sdp: pc.localDescription.sdp });
    offered = true; pendingIce.forEach(send); pendingIce = [];
  } else if (m.t === 'answer' && pc) await pc.setRemoteDescription({ type: 'answer', sdp: m.sdp });
  else if (m.t === 'ice' && pc) await pc.addIceCandidate(m.candidate);
}
// ICE pode chegar durante setRemoteDescription: mantém a ordem do canal.
let incoming = Promise.resolve();
if (typeof window !== 'undefined') {
  window.chrome.webview.addEventListener('message', e => {
    incoming = incoming.then(() => receive(e.data)).catch(() => { send({ t: 'error' }); stop(); });
  });
  window.addEventListener('pagehide', stop);
  send({ t: 'ready' });
}
