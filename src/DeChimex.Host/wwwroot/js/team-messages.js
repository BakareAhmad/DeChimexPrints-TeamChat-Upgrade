/*
 * Internal Team Chat client.
 * Uses the authenticated notifications hub for live delivery and persisted
 * conversation reads for reconnect reconciliation. Uploads are ordinary
 * multipart requests to the existing internal-message endpoint; private media
 * uses its own TeamChatMedia storage category and participant authorization.
 */
(function () {
  'use strict';

  var script = document.currentScript;
  var base = (script && script.getAttribute('data-base')) || '/admin/team-messages';
  var toasts = (script && script.getAttribute('data-toasts')) === 'true';
  var badge = document.getElementById('teamMsgBadge');
  var page = document.querySelector('[data-team-conversation]');
  var pageConversationId = page ? (page.getAttribute('data-team-conversation') || '') : '';
  var myId = page ? (page.getAttribute('data-my-id') || '').toLowerCase() : '';
  var syncUrl = page ? (page.getAttribute('data-team-sync-url') || '') : '';
  var readUrl = page ? (page.getAttribute('data-team-read-url') || '') : '';
  var messagesList = page ? page.querySelector('[data-team-messages-list]') : null;
  var messagesScroll = page ? page.querySelector('[data-team-messages-scroll]') : null;
  var livePill = page ? page.querySelector('[data-team-live-status]') : null;
  var liveLabel = page ? page.querySelector('[data-team-live-label]') : null;

  var SVG_NS = 'http://www.w3.org/2000/svg';
  var ICONS = {
    image: [['rect', { x: '3', y: '3', width: '18', height: '18', rx: '3' }], ['circle', { cx: '8.5', cy: '8.5', r: '1.5' }], ['path', { d: 'm21 15-5-5L5 21' }]],
    video: [['rect', { x: '3', y: '5', width: '13', height: '14', rx: '2' }], ['path', { d: 'm16 10 5-3v10l-5-3' }]],
    audio: [['path', { d: 'M12 2a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z' }], ['path', { d: 'M19 10v1a7 7 0 0 1-14 0v-1M12 18v4m-4 0h8' }]],
    file: [['path', { d: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z' }], ['path', { d: 'M14 2v6h6M8 13h8M8 17h8' }]],
    remove: [['path', { d: 'm6 6 12 12M18 6 6 18' }]],
    checks: [['path', { d: 'm1 6 3.5 3.5L12 2' }], ['path', { d: 'm6 6 3.5 3.5L17 2' }]]
  };

  function svgIcon(name, className) {
    var svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('viewBox', name === 'checks' ? '0 0 18 12' : '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('focusable', 'false');
    if (className) svg.setAttribute('class', className);
    (ICONS[name] || ICONS.file).forEach(function (item) {
      var node = document.createElementNS(SVG_NS, item[0]);
      Object.keys(item[1]).forEach(function (key) { node.setAttribute(key, item[1][key]); });
      svg.appendChild(node);
    });
    return svg;
  }

  function csrf() {
    var meta = document.querySelector('meta[name="csrf-token"]');
    if (meta) return meta.getAttribute('content') || '';
    var input = document.querySelector('input[name="__RequestVerificationToken"]');
    return input ? input.value : '';
  }

  function fmtClock(totalSeconds) {
    var seconds = Math.max(0, Math.floor(Number(totalSeconds) || 0));
    return Math.floor(seconds / 60) + ':' + ('0' + (seconds % 60)).slice(-2);
  }

  function fmtTime(iso) {
    var date = new Date(iso);
    if (isNaN(date.getTime())) return '';
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  function fmtFull(iso) {
    var date = new Date(iso);
    if (isNaN(date.getTime())) return '';
    return date.toLocaleString([], { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
  }

  function fmtSize(bytes) {
    var size = Number(bytes) || 0;
    if (size < 1024) return size + ' B';
    if (size < 1024 * 1024) return (size / 1024).toFixed(1) + ' KB';
    return (size / 1024 / 1024).toFixed(1) + ' MB';
  }

  function safeUrl(value) {
    if (!value) return '';
    try {
      var url = new URL(String(value), window.location.href);
      if (url.protocol !== 'http:' && url.protocol !== 'https:') return '';
      return url.href;
    } catch (e) { return ''; }
  }

  function labelForKind(kind) {
    if (kind === 'image') return 'Photo';
    if (kind === 'video') return 'Video';
    if (kind === 'voice') return 'Voice note';
    if (kind === 'audio') return 'Audio';
    return 'Attachment';
  }

  function fileKind(file) {
    var mime = String(file.type || '').toLowerCase();
    var name = String(file.name || '').toLowerCase();
    if (mime.indexOf('image/') === 0 || /\.(png|jpe?g|webp|gif)$/.test(name)) return 'image';
    if (mime.indexOf('video/') === 0 || /\.(mp4|webm)$/.test(name)) return 'video';
    if (mime.indexOf('audio/') === 0 || /\.(mp3|ogg|oga|wav|m4a|webm)$/.test(name)) return 'audio';
    return 'file';
  }

  /* ---------- unread count and inbox filtering ---------- */
  function fetchCount() {
    fetch(base + '/unread-count', {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' }
    }).then(function (response) { return response.ok ? response.json() : null; })
      .then(function (data) {
        if (!badge || !data || typeof data.count !== 'number') return;
        if (data.count > 0) {
          badge.textContent = data.count > 99 ? '99+' : String(data.count);
          badge.hidden = false;
        } else {
          badge.hidden = true;
        }
      })
      .catch(function () { /* keep the last server-rendered count */ });
  }

  (function wireInboxFilter() {
    var search = document.querySelector('[data-team-search]');
    var list = document.querySelector('[data-team-conversation-list]');
    var noResults = document.querySelector('[data-team-no-results]');
    if (!search || !list) return;
    var items = Array.prototype.slice.call(list.querySelectorAll('[data-team-conversation-item]'));
    search.addEventListener('input', function () {
      var query = String(search.value || '').trim().toLowerCase();
      var visible = 0;
      items.forEach(function (item) {
        var haystack = item.getAttribute('data-search') || item.textContent || '';
        var match = !query || haystack.toLowerCase().indexOf(query) >= 0;
        item.hidden = !match;
        if (match) visible++;
      });
      if (noResults) noResults.hidden = visible > 0;
    });
  })();

  /* ---------- safe message rendering and chronological insertion ---------- */
  function nearBottom() {
    if (!messagesScroll) return true;
    return messagesScroll.scrollHeight - messagesScroll.scrollTop - messagesScroll.clientHeight < 130;
  }

  function compareNodes(left, right) {
    var leftTime = Date.parse(left.getAttribute('data-sent-utc') || '') || 0;
    var rightTime = Date.parse(right.getAttribute('data-sent-utc') || '') || 0;
    if (leftTime !== rightTime) return leftTime - rightTime;
    var leftId = left.getAttribute('data-team-msg') || '';
    var rightId = right.getAttribute('data-team-msg') || '';
    return leftId < rightId ? -1 : (leftId > rightId ? 1 : 0);
  }

  function updateReadState(node, readAtUtc) {
    if (!node || !node.classList.contains('is-mine')) return;
    var stamp = readAtUtc || '';
    node.setAttribute('data-read-at', stamp);
    var state = stamp ? 'read' : 'sent';
    var status = node.querySelector('[data-team-read-state]');
    if (!status) return;
    status.className = 'team-message-status is-' + state;
    status.setAttribute('data-team-read-state', state);
    status.setAttribute('aria-label', state === 'read' ? 'Read' : 'Sent');
    status.title = state === 'read' ? 'Read' : 'Sent';
    var text = status.querySelector('span');
    if (text) text.textContent = state === 'read' ? 'Read' : 'Sent';
  }

  function addAttachment(parent, attachment) {
    if (!attachment) return;
    var url = safeUrl(attachment.url);
    if (!url) return;
    var kind = String(attachment.kind || 'file').toLowerCase();
    var name = attachment.originalFileName || 'Attachment';
    var contentType = attachment.contentType || '';

    if (kind === 'image') {
      var imageLink = document.createElement('a');
      imageLink.className = 'team-image-attachment';
      imageLink.href = url;
      imageLink.target = '_blank';
      imageLink.rel = 'noopener noreferrer';
      imageLink.setAttribute('aria-label', 'Open image: ' + name);
      var image = document.createElement('img');
      image.src = url;
      image.alt = name;
      image.loading = 'lazy';
      image.decoding = 'async';
      imageLink.appendChild(image);
      parent.appendChild(imageLink);
      return;
    }

    if (kind === 'video') {
      var figure = document.createElement('figure');
      figure.className = 'team-video-attachment';
      var video = document.createElement('video');
      video.controls = true;
      video.preload = 'metadata';
      video.playsInline = true;
      video.setAttribute('aria-label', 'Video: ' + name);
      var videoSource = document.createElement('source');
      videoSource.src = url;
      if (contentType) videoSource.type = contentType;
      video.appendChild(videoSource);
      video.appendChild(document.createTextNode('Your browser does not support video playback.'));
      var caption = document.createElement('figcaption');
      caption.textContent = name;
      figure.appendChild(video);
      figure.appendChild(caption);
      parent.appendChild(figure);
      return;
    }

    if (kind === 'audio' || kind === 'voice') {
      var audioWrap = document.createElement('div');
      audioWrap.className = 'team-audio-attachment';
      var audioIcon = document.createElement('span');
      audioIcon.className = 'team-media-icon';
      audioIcon.setAttribute('aria-hidden', 'true');
      audioIcon.appendChild(svgIcon('audio'));
      var audioMain = document.createElement('div');
      audioMain.className = 'team-audio-main';
      var audioLabel = document.createElement('span');
      audioLabel.className = 'team-audio-label';
      audioLabel.appendChild(document.createTextNode(labelForKind(kind) + ' '));
      var fileName = document.createElement('span');
      fileName.className = 'team-file-name';
      fileName.textContent = '· ' + name;
      audioLabel.appendChild(fileName);
      var audio = document.createElement('audio');
      audio.controls = true;
      audio.preload = 'none';
      audio.setAttribute('aria-label', labelForKind(kind) + ': ' + name);
      var audioSource = document.createElement('source');
      audioSource.src = url;
      if (contentType) audioSource.type = contentType;
      audio.appendChild(audioSource);
      audio.appendChild(document.createTextNode('Your browser does not support audio playback.'));
      audioMain.appendChild(audioLabel);
      audioMain.appendChild(audio);
      audioWrap.appendChild(audioIcon);
      audioWrap.appendChild(audioMain);
      if (attachment.durationSeconds) {
        var duration = document.createElement('span');
        duration.className = 'team-audio-duration';
        duration.textContent = fmtClock(attachment.durationSeconds);
        audioWrap.appendChild(duration);
      }
      parent.appendChild(audioWrap);
      return;
    }

    var fileLink = document.createElement('a');
    fileLink.className = 'team-file-attachment';
    fileLink.href = url;
    fileLink.target = '_blank';
    fileLink.rel = 'noopener noreferrer';
    var fileIcon = document.createElement('span');
    fileIcon.className = 'team-media-icon';
    fileIcon.setAttribute('aria-hidden', 'true');
    fileIcon.appendChild(svgIcon('file'));
    var fileCopy = document.createElement('span');
    fileCopy.className = 'team-file-copy';
    var fileTitle = document.createElement('strong');
    fileTitle.textContent = name;
    var fileSize = document.createElement('small');
    fileSize.textContent = fmtSize(attachment.sizeBytes);
    fileCopy.appendChild(fileTitle);
    fileCopy.appendChild(fileSize);
    var download = svgIcon('file', 'team-download-icon');
    fileLink.appendChild(fileIcon);
    fileLink.appendChild(fileCopy);
    fileLink.appendChild(download);
    parent.appendChild(fileLink);
  }

  function makeMessage(message) {
    if (!message) return null;
    var id = message.id || message.messageId;
    if (!id) return null;
    var senderId = String(message.senderId || '').toLowerCase();
    var mine = message.isMine === true || (myId && senderId === myId);
    var sentAt = message.sentAtUtc || message.sentUtc || new Date().toISOString();
    var item = document.createElement('li');
    item.className = 'team-message ' + (mine ? 'is-mine' : 'is-theirs');
    item.setAttribute('data-team-msg', String(id));
    item.setAttribute('data-sent-utc', sentAt);
    item.setAttribute('data-sender-id', senderId);
    item.setAttribute('data-read-at', message.readAtUtc || '');
    item.setAttribute('data-client-message-id', message.clientMessageId || '');

    var bubble = document.createElement('div');
    bubble.className = 'team-bubble';
    if (!mine) {
      var author = document.createElement('div');
      author.className = 'team-message-author';
      author.textContent = message.senderName || 'Colleague';
      bubble.appendChild(author);
    }

    if (message.body) {
      var body = document.createElement('p');
      body.className = 'team-message-text';
      body.textContent = message.body;
      bubble.appendChild(body);
    }

    if (message.attachments && message.attachments.length) {
      var attachmentList = document.createElement('div');
      attachmentList.className = 'team-attachments';
      message.attachments.forEach(function (attachment) { addAttachment(attachmentList, attachment); });
      if (attachmentList.childNodes.length) bubble.appendChild(attachmentList);
    }

    var footer = document.createElement('footer');
    footer.className = 'team-message-foot';
    var time = document.createElement('time');
    time.dateTime = sentAt;
    time.title = fmtFull(sentAt);
    time.textContent = fmtTime(sentAt);
    footer.appendChild(time);
    if (mine) {
      var status = document.createElement('span');
      var read = !!message.readAtUtc;
      status.className = 'team-message-status is-' + (read ? 'read' : 'sent');
      status.setAttribute('data-team-read-state', read ? 'read' : 'sent');
      status.setAttribute('aria-label', read ? 'Read' : 'Sent');
      status.title = read ? 'Read' : 'Sent';
      status.appendChild(svgIcon('checks'));
      var stateText = document.createElement('span');
      stateText.textContent = read ? 'Read' : 'Sent';
      status.appendChild(stateText);
      footer.appendChild(status);
    }
    bubble.appendChild(footer);
    item.appendChild(bubble);
    return item;
  }

  function findMessage(id) {
    if (!messagesList || !id) return null;
    var nodes = messagesList.querySelectorAll('[data-team-msg]');
    for (var i = 0; i < nodes.length; i++) {
      if (nodes[i].getAttribute('data-team-msg') === String(id)) return nodes[i];
    }
    return null;
  }

  function appendMessage(message) {
    if (!messagesList || !message) return null;
    var id = message.id || message.messageId;
    if (!id) return null;
    var existing = findMessage(id);
    if (existing) {
      if (message.readAtUtc) updateReadState(existing, message.readAtUtc);
      return existing;
    }

    var node = makeMessage(message);
    if (!node) return null;
    var stick = nearBottom();
    var empty = messagesList.querySelector('[data-team-empty]');
    if (empty) empty.remove();
    var current = Array.prototype.slice.call(messagesList.querySelectorAll('[data-team-msg]'));
    var before = null;
    for (var i = 0; i < current.length; i++) {
      if (compareNodes(current[i], node) > 0) { before = current[i]; break; }
    }
    messagesList.insertBefore(node, before);
    if (stick && messagesScroll) messagesScroll.scrollTop = messagesScroll.scrollHeight;
    return node;
  }

  function applyReadEvent(payload) {
    if (!payload || payload.conversationId !== pageConversationId || !payload.messageIds) return;
    var now = new Date().toISOString();
    payload.messageIds.forEach(function (id) { updateReadState(findMessage(id), now); });
  }

  function latestSentUtc() {
    if (!messagesList) return '';
    var nodes = messagesList.querySelectorAll('[data-team-msg]');
    var max = 0;
    var value = '';
    for (var i = 0; i < nodes.length; i++) {
      var candidate = nodes[i].getAttribute('data-sent-utc') || '';
      var time = Date.parse(candidate) || 0;
      if (time > max) { max = time; value = candidate; }
    }
    return value;
  }

  function syncMessages() {
    if (!page || !syncUrl) return Promise.resolve();
    var cursor = latestSentUtc();
    var url = syncUrl + (cursor ? ('?afterUtc=' + encodeURIComponent(cursor)) : '');
    return fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (response) {
        if (!response.ok) throw new Error('Message sync failed');
        return response.json();
      })
      .then(function (data) {
        (data && data.messages || []).forEach(function (message) { appendMessage(message); });
        setLiveState('Live updates on', 'online');
      });
  }

  function setLiveState(label, state) {
    if (!livePill || !liveLabel) return;
    liveLabel.textContent = label;
    livePill.classList.remove('is-online', 'is-offline', 'is-reconnecting');
    if (state) livePill.classList.add('is-' + state);
  }

  var readTimer = null;
  function postRead() {
    if (!readUrl || !page) return Promise.resolve();
    return fetch(readUrl, {
      method: 'POST', credentials: 'same-origin',
      headers: { RequestVerificationToken: csrf(), Accept: 'application/json' }
    }).then(function (response) {
      if (!response.ok) throw new Error('Read state could not be saved');
      fetchCount();
    }).catch(function () { /* the next open/reconnect reconciles persisted read state */ });
  }

  function scheduleRead() {
    if (document.visibilityState !== 'visible') return;
    if (readTimer) clearTimeout(readTimer);
    readTimer = setTimeout(function () { readTimer = null; postRead(); }, 220);
  }

  /* ---------- composer: attachments, voice, progress, retry-safe send ---------- */
  var form = page ? page.querySelector('[data-team-send-form]') : null;
  var bodyInput = page ? page.querySelector('[data-team-body]') : null;
  var fileInput = page ? page.querySelector('[data-team-files]') : null;
  var attachLabel = page ? page.querySelector('[data-team-attach]') : null;
  var recordButton = page ? page.querySelector('[data-team-record]') : null;
  var stageList = page ? page.querySelector('[data-team-stage-list]') : null;
  var sendButton = page ? page.querySelector('[data-team-send]') : null;
  var clientIdInput = page ? page.querySelector('#teamClientMessageId') : null;
  var errorBox = page ? page.querySelector('[data-team-error]') : null;
  var progress = page ? page.querySelector('[data-team-upload-progress]') : null;
  var progressFill = page ? page.querySelector('[data-team-upload-fill]') : null;
  var characterCount = page ? page.querySelector('[data-team-character-count]') : null;
  var recordingBar = page ? page.querySelector('[data-team-recording-bar]') : null;
  var recordingTime = page ? page.querySelector('[data-team-recording-time]') : null;
  var recordStop = page ? page.querySelector('[data-team-record-stop]') : null;
  var recordCancel = page ? page.querySelector('[data-team-record-cancel]') : null;
  var voicePreview = page ? page.querySelector('[data-team-voice-preview]') : null;
  var voiceAudio = page ? page.querySelector('[data-team-voice-audio]') : null;
  var voiceDiscard = page ? page.querySelector('[data-team-voice-discard]') : null;
  var staged = [];
  var recordedVoice = null;
  var sending = false;
  var recorder = null;
  var recorderStream = null;
  var recorderChunks = [];
  var recorderMime = '';
  var recorderStart = 0;
  var recorderTimer = null;
  var recorderCancelled = false;
  var recordingPending = false;
  var recordingRequestId = 0;
  var MAX_BYTES = 10 * 1024 * 1024;
  var MAX_FILES = 6;
  var MAX_RECORDING_SECONDS = 300;
  var ALLOWED_EXTENSIONS = {
    '.jpg': true, '.jpeg': true, '.png': true, '.webp': true, '.gif': true,
    '.mp4': true, '.webm': true, '.mp3': true, '.ogg': true, '.oga': true,
    '.wav': true, '.m4a': true, '.pdf': true, '.doc': true, '.docx': true
  };

  function showError(message) {
    if (!errorBox) return;
    errorBox.textContent = message || 'The message could not be sent.';
    errorBox.hidden = false;
  }

  function hideError() {
    if (!errorBox) return;
    errorBox.textContent = '';
    errorBox.hidden = true;
  }

  function newClientId() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') return window.crypto.randomUUID();
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (ch) {
      var random = Math.random() * 16 | 0;
      return (ch === 'x' ? random : (random & 3 | 8)).toString(16);
    });
  }

  function updateCharacterCount() {
    if (characterCount && bodyInput)
      characterCount.textContent = String(bodyInput.value.length) + ' / 2000';
  }

  function revokeItem(item) {
    if (item && item.previewUrl) {
      try { URL.revokeObjectURL(item.previewUrl); } catch (e) { /* already released */ }
      item.previewUrl = '';
    }
  }

  function removeStaged(item) {
    var index = staged.indexOf(item);
    if (index < 0) return;
    staged.splice(index, 1);
    if (recordedVoice === item) clearVoicePreview();
    revokeItem(item);
    renderStage();
  }

  function renderStage() {
    if (!stageList) return;
    while (stageList.firstChild) stageList.removeChild(stageList.firstChild);
    stageList.hidden = staged.length === 0;
    staged.forEach(function (item) {
      var row = document.createElement('div');
      row.className = 'team-stage-item';
      var thumb = document.createElement('span');
      thumb.className = 'team-stage-thumb';
      if (item.kind === 'image' && item.previewUrl) {
        var image = document.createElement('img');
        image.src = item.previewUrl;
        image.alt = '';
        thumb.appendChild(image);
      } else {
        thumb.appendChild(svgIcon(item.kind === 'video' ? 'video' : (item.kind === 'audio' || item.kind === 'voice' ? 'audio' : 'file')));
      }
      var meta = document.createElement('span');
      meta.className = 'team-stage-meta';
      var title = document.createElement('strong');
      title.textContent = item.file.name || 'Attachment';
      var detail = document.createElement('small');
      detail.textContent = (item.kind === 'voice' ? 'Voice note · ' : '') + fmtSize(item.file.size);
      meta.appendChild(title);
      meta.appendChild(detail);
      var remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'team-stage-remove';
      remove.setAttribute('aria-label', 'Remove ' + (item.file.name || 'attachment'));
      remove.title = 'Remove attachment';
      remove.appendChild(svgIcon('remove'));
      remove.addEventListener('click', function () { removeStaged(item); });
      row.appendChild(thumb);
      row.appendChild(meta);
      row.appendChild(remove);
      stageList.appendChild(row);
    });
    if (recordButton) recordButton.disabled = sending || staged.length >= MAX_FILES || !!recorder;
    if (sendButton) sendButton.disabled = sending || !!recorder;
  }

  function addFile(file, kind, duration, voice) {
    var previewUrl = '';
    if (kind === 'image' || voice) {
      try { previewUrl = URL.createObjectURL(file); } catch (e) { previewUrl = ''; }
    }
    var item = { file: file, kind: kind, duration: duration || null, previewUrl: previewUrl, voice: !!voice };
    staged.push(item);
    if (voice) {
      recordedVoice = item;
      if (voiceAudio && previewUrl) voiceAudio.src = previewUrl;
      if (voicePreview) voicePreview.hidden = false;
    }
    renderStage();
  }

  function acceptFiles(fileList) {
    if (!fileList || !fileList.length || sending || recorder || recordingPending) return;
    hideError();
    var rejected = [];
    for (var i = 0; i < fileList.length; i++) {
      var file = fileList[i];
      if (staged.length >= MAX_FILES) { rejected.push('Only 6 files can be attached to a message.'); break; }
      if (!file || file.size <= 0) { rejected.push('Empty files cannot be attached.'); continue; }
      if (file.size > MAX_BYTES) { rejected.push(file.name + ' is larger than 10 MB.'); continue; }
      var dot = String(file.name || '').lastIndexOf('.');
      var extension = dot >= 0 ? String(file.name).slice(dot).toLowerCase() : '';
      if (!ALLOWED_EXTENSIONS[extension]) { rejected.push(file.name + ' is not an allowed file type.'); continue; }
      addFile(file, fileKind(file), null, false);
    }
    if (rejected.length) showError(rejected[0]);
    if (fileInput) fileInput.value = '';
  }

  function clearVoicePreview() {
    recordedVoice = null;
    if (voiceAudio) {
      voiceAudio.removeAttribute('src');
      try { voiceAudio.load(); } catch (e) { /* not critical */ }
    }
    if (voicePreview) voicePreview.hidden = true;
  }

  function clearComposer() {
    if (bodyInput) bodyInput.value = '';
    staged.forEach(revokeItem);
    staged = [];
    clearVoicePreview();
    if (fileInput) fileInput.value = '';
    if (progress) progress.hidden = true;
    if (progressFill) progressFill.style.width = '0';
    updateCharacterCount();
    renderStage();
    hideError();
    if (clientIdInput) clientIdInput.value = newClientId();
  }

  function setSending(value) {
    sending = !!value;
    if (sendButton) sendButton.disabled = sending || !!recorder;
    if (bodyInput) bodyInput.disabled = sending;
    if (attachLabel) attachLabel.setAttribute('aria-disabled', sending ? 'true' : 'false');
    if (recordButton) recordButton.disabled = sending || staged.length >= MAX_FILES || !!recorder;
    if (sendButton) {
      var label = sendButton.querySelector('span');
      if (label) label.textContent = sending ? 'Sending…' : 'Send';
    }
  }

  function stopRecorderTracks() {
    if (!recorderStream) return;
    recorderStream.getTracks().forEach(function (track) { try { track.stop(); } catch (e) { /* no-op */ } });
    recorderStream = null;
  }

  function setRecordingUi(active) {
    if (recordingBar) recordingBar.hidden = !active;
    if (recordButton) recordButton.disabled = active || sending || staged.length >= MAX_FILES;
    if (sendButton) sendButton.disabled = active || sending;
    if (bodyInput) bodyInput.disabled = active || sending;
    if (attachLabel) attachLabel.setAttribute('aria-disabled', active || sending ? 'true' : 'false');
  }

  function finishRecorder(cancelled) {
    if (!recorder) {
      if (recordingPending) {
        // getUserMedia cannot be aborted consistently, but invalidating this request
        // ensures a late permission grant only releases its stream instead of starting
        // a recording after the user already cancelled/stopped the request.
        recordingPending = false;
        recordingRequestId++;
        recorderCancelled = !!cancelled;
      }
      if (recorderTimer) { clearInterval(recorderTimer); recorderTimer = null; }
      stopRecorderTracks();
      setRecordingUi(false);
      return;
    }
    recorderCancelled = !!cancelled;
    if (recorderTimer) { clearInterval(recorderTimer); recorderTimer = null; }
    try {
      if (recorder.state !== 'inactive') recorder.stop();
      else recorder.onstop();
    } catch (e) {
      recorder.onstop();
    }
  }

  function startRecording() {
    if (!recordButton || staged.length >= MAX_FILES || sending || recorder || recordingPending) return;
    hideError();
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !window.MediaRecorder) {
      showError('Voice recording is not supported in this browser. You can still upload an audio file.');
      return;
    }
    setRecordingUi(true);
    recordingPending = true;
    recorderCancelled = false;
    var requestId = ++recordingRequestId;
    navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
      if (!recordingPending || requestId !== recordingRequestId) {
        stream.getTracks().forEach(function (track) { try { track.stop(); } catch (e) { /* released below if needed */ } });
        return;
      }
      recordingPending = false;
      recorderStream = stream;
      recorderChunks = [];
      recorderCancelled = false;
      recorderStart = Date.now();
      var candidates = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/mp4'];
      recorderMime = '';
      for (var i = 0; i < candidates.length; i++) {
        try {
          if (MediaRecorder.isTypeSupported && MediaRecorder.isTypeSupported(candidates[i])) {
            recorderMime = candidates[i];
            break;
          }
        } catch (e) { /* browser exposes no MIME query; default recorder choice */ }
      }
      try { recorder = new MediaRecorder(stream, recorderMime ? { mimeType: recorderMime } : undefined); }
      catch (e) {
        recorder = null;
        stopRecorderTracks();
        setRecordingUi(false);
        showError('Voice recording could not start on this device.');
        return;
      }
      recorder.ondataavailable = function (event) {
        if (event.data && event.data.size) recorderChunks.push(event.data);
      };
      recorder.onerror = function () {
        showError('Voice recording stopped unexpectedly. Please try again.');
        finishRecorder(true);
      };
      recorder.onstop = function () {
        var completedRecorder = recorder;
        recorder = null;
        var actualMime = (completedRecorder && completedRecorder.mimeType) || recorderMime || 'audio/webm';
        var duration = Math.max(1, Math.round((Date.now() - recorderStart) / 1000));
        var chunks = recorderChunks;
        recorderChunks = [];
        stopRecorderTracks();
        setRecordingUi(false);
        if (recorderCancelled) return;
        var blob = new Blob(chunks, { type: actualMime.split(';')[0] });
        if (!blob.size) {
          showError('The recording was empty. Please try again.');
          return;
        }
        if (blob.size > MAX_BYTES) {
          showError('This voice note is larger than the 10 MB file limit. Record a shorter note or lower the microphone quality.');
          return;
        }
        var extension = actualMime.indexOf('ogg') >= 0 ? '.ogg' : (actualMime.indexOf('mp4') >= 0 ? '.m4a' : '.webm');
        var voiceFile;
        try { voiceFile = new File([blob], 'voice-note-' + Date.now() + extension, { type: actualMime.split(';')[0] }); }
        catch (e) { voiceFile = blob; voiceFile.name = 'voice-note-' + Date.now() + extension; }
        addFile(voiceFile, 'voice', Math.min(duration, MAX_RECORDING_SECONDS), true);
      };
      try { recorder.start(250); }
      catch (e) {
        finishRecorder(true);
        showError('Voice recording could not start on this device.');
        return;
      }
      if (recordingTime) recordingTime.textContent = '0:00 / 5:00';
      recorderTimer = setInterval(function () {
        var elapsed = Math.floor((Date.now() - recorderStart) / 1000);
        if (recordingTime) recordingTime.textContent = fmtClock(elapsed) + ' / 5:00';
        if (elapsed >= MAX_RECORDING_SECONDS) finishRecorder(false);
      }, 300);
    }).catch(function (error) {
      if (requestId !== recordingRequestId) return;
      recordingPending = false;
      stopRecorderTracks();
      setRecordingUi(false);
      var name = error && error.name || '';
      showError(name === 'NotAllowedError' || name === 'SecurityError'
        ? 'Microphone access was denied. Allow microphone access and try again.'
        : 'No microphone is available on this device.');
    });
  }

  function sendMessage() {
    if (!form || sending || recorder || recordingPending) return;
    hideError();
    var text = bodyInput ? bodyInput.value : '';
    if (!text.trim() && staged.length === 0) {
      showError('Write a message or attach a file before sending.');
      if (bodyInput) bodyInput.focus();
      return;
    }
    if (text.length > 2000) {
      showError('Messages are limited to 2000 characters.');
      return;
    }

    var formData = new FormData();
    formData.append('body', text);
    formData.append('clientMessageId', clientIdInput && clientIdInput.value || newClientId());
    formData.append('__RequestVerificationToken', csrf());
    staged.forEach(function (item) {
      formData.append('files', item.file, item.file.name || 'voice-note.webm');
      formData.append('kinds', item.kind);
      formData.append('durations', item.duration ? String(item.duration) : '');
    });

    setSending(true);
    if (progress) progress.hidden = staged.length === 0;
    if (progressFill) progressFill.style.width = '0';
    var xhr = new XMLHttpRequest();
    xhr.open('POST', form.action, true);
    xhr.setRequestHeader('Accept', 'application/json');
    xhr.timeout = 120000;
    if (xhr.upload && staged.length) {
      xhr.upload.onprogress = function (event) {
        if (event.lengthComputable && progressFill)
          progressFill.style.width = Math.round(event.loaded / event.total * 100) + '%';
      };
    }
    xhr.onload = function () {
      setSending(false);
      if (progress) progress.hidden = true;
      var data = null;
      try { data = JSON.parse(xhr.responseText); } catch (e) { data = null; }
      if (xhr.status >= 200 && xhr.status < 300 && data && data.message) {
        appendMessage(data.message); // push + response + reconnect history dedupe by persisted id
        clearComposer();
        fetchCount();
        return;
      }
      var error = data && data.error || ('The message could not be sent (HTTP ' + xhr.status + ').');
      showError(error + ' Your draft was kept. Press Send to retry.');
    };
    xhr.onerror = function () {
      setSending(false);
      if (progress) progress.hidden = true;
      showError('The connection dropped before the response arrived. Your draft was kept. Press Send to retry.');
    };
    xhr.ontimeout = xhr.onerror;
    xhr.send(formData);
  }

  if (fileInput) fileInput.addEventListener('change', function () { acceptFiles(fileInput.files); });
  if (attachLabel) attachLabel.addEventListener('keydown', function (event) {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      if (!sending && fileInput) fileInput.click();
    }
  });
  if (recordButton) {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !window.MediaRecorder)
      recordButton.hidden = true;
    else recordButton.addEventListener('click', startRecording);
  }
  if (recordStop) recordStop.addEventListener('click', function () { finishRecorder(false); });
  if (recordCancel) recordCancel.addEventListener('click', function () { finishRecorder(true); });
  if (voiceDiscard) voiceDiscard.addEventListener('click', function () {
    if (recordedVoice) removeStaged(recordedVoice);
  });
  if (form) form.addEventListener('submit', function (event) {
    event.preventDefault();
    sendMessage();
  });
  if (bodyInput) {
    bodyInput.addEventListener('input', updateCharacterCount);
    bodyInput.addEventListener('keydown', function (event) {
      if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
        event.preventDefault();
        if (form && form.requestSubmit) form.requestSubmit();
        else sendMessage();
      }
    });
    updateCharacterCount();
  }

  /* ---------- authenticated SignalR delivery and reconnect recovery ---------- */
  if (badge) fetchCount();
  if (!badge && !page) return;
  if (page && messagesScroll) messagesScroll.scrollTop = messagesScroll.scrollHeight;

  if (!window.signalR) {
    setLiveState('Live updates unavailable', 'offline');
    return; // message send/history still work; no polling fallback
  }

  var connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/notifications', { withCredentials: true })
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on('internalMessage', function (message) {
    if (!message) return;
    var sameConversation = pageConversationId && String(message.conversationId) === pageConversationId;
    if (sameConversation) {
      appendMessage(message);
      if (String(message.senderId || '').toLowerCase() !== myId && document.visibilityState === 'visible')
        scheduleRead();
      fetchCount();
      return;
    }
    fetchCount();
    if (toasts && message.senderId && String(message.senderId).toLowerCase() !== myId && window.DeChimexToast) {
      var preview = String(message.body || '').trim();
      if (!preview && message.attachments && message.attachments.length)
        preview = labelForKind(message.attachments[0].kind) + (message.attachments.length > 1 ? ' + ' + (message.attachments.length - 1) + ' more' : '');
      if (preview.length > 140) preview = preview.slice(0, 140) + '…';
      window.DeChimexToast.show('Message from ' + (message.senderName || 'a colleague'), preview,
        { href: base + '/' + message.conversationId, text: 'Open' });
    }
  });

  connection.on('internalMessagesRead', applyReadEvent);
  connection.onreconnecting(function () { setLiveState('Reconnecting…', 'reconnecting'); });
  connection.onreconnected(function () {
    setLiveState('Syncing…', 'reconnecting');
    fetchCount();
    syncMessages().then(function () {
      if (document.visibilityState === 'visible') scheduleRead();
    }).catch(function () { setLiveState('Live updates delayed', 'offline'); });
  });
  connection.onclose(function () { setLiveState('Live updates paused', 'offline'); });

  setLiveState('Connecting…', 'reconnecting');
  connection.start().then(function () {
    setLiveState('Live updates on', 'online');
    fetchCount();
    return syncMessages();
  }).then(function () {
    if (page && messagesScroll) messagesScroll.scrollTop = messagesScroll.scrollHeight;
  }).catch(function () {
    setLiveState('Live updates unavailable', 'offline');
    // SignalR automatic reconnect is deliberately preferred to a polling loop.
  });

  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState !== 'visible') return;
    fetchCount();
    if (page && connection.state === signalR.HubConnectionState.Connected) {
      syncMessages().then(scheduleRead).catch(function () { /* reconnect callback retries */ });
    }
  });
})();
