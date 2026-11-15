import * as ncw from 'nextcowork'

export function activate(context) {
  context.subscriptions.push(
    ncw.tools.registerTool('pick_one', {
      description: 'Ask the user to pick one of two options with a live card.',
      inputSchema: { type: 'object', properties: { question: { type: 'string' } }, required: ['question'] },
      readOnly: true,
      interactive: true,
      async invoke({ input, progress, onAction }) {
        progress({
          message: 'Waiting for the user',
          card: { kind: 'declarative', blocks: [
            { type: 'text', value: String(input.question) },
            { type: 'button', actionId: 'yes', label: 'Yes', tone: 'ok' },
            { type: 'button', actionId: 'no', label: 'No', tone: 'danger', confirm: { titleKey: 'plugin.acme.card.tool.pick' } }
          ] }
        })
        const action = await new Promise((resolve) => onAction((a) => resolve(a)))
        return { content: [{ text: 'picked:' + action.actionId + ':' + JSON.stringify(action.value ?? null) }] }
      }
    })
  )
}
