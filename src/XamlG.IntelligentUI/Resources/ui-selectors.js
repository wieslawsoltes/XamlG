// Logical selector projection: native slot wrappers are never counted as authored children.
// All strings below are generated from the bounded AST, not forwarded model CSS.
const uiStyleBases={StackPanel:'Panel',Grid:'Panel',Canvas:'Panel',WrapPanel:'Panel',DockPanel:'Panel',UniformGrid:'Panel',
  RepeatButton:'Button',ToggleButton:'Button',CheckBox:'ToggleButton',RadioButton:'ToggleButton',ToggleSwitch:'ToggleButton',
  Button:'ContentControl',UserControl:'ContentControl',ScrollViewer:'ContentControl',Expander:'ContentControl',TabItem:'ContentControl',
  ListBoxItem:'ContentControl',ComboBoxItem:'ListBoxItem',Label:'ContentControl',ListBox:'ItemsControl',ComboBox:'ItemsControl',
  TabControl:'ItemsControl',TreeView:'ItemsControl',TreeViewItem:'ItemsControl',SelectableTextBlock:'TextBlock'};
function uiSelectorType(actual,expected,derived){
  if(!expected)return true;
  for(let current=actual;current;current=derived?uiStyleBases[current]:null)if(current===expected)return true;
  return false;
}
function parseUiSelector(source){
  if(typeof source!=='string'||!source.length||source.length>512)throw new Error('Invalid style selector.');
  let p=0,predicates=0;const steps=[];
  const white=()=>{const start=p;while(p<source.length&&/\s/.test(source[p]))p++;return p!==start;};
  const take=value=>{if(!source.startsWith(value,p))return false;p+=value.length;return true;};
  const require=value=>{if(!take(value))throw new Error('Incomplete selector function.');};
  const id=()=>{const start=p;while(p<source.length&&/[A-Za-z0-9_-]/.test(source[p]))p++;const name=source.slice(start,p);if(!styleIdentifier.test(name))throw new Error('Invalid selector identifier.');return name;};
  const known=name=>{if(!types.has(name)&&!widgets.has(name)&&!['ContentPresenter','ItemsPresenter'].includes(name))throw new Error('Unknown selector type.');return name;};
  function position(text,fromEnd){
    if(text.length>64)throw new Error('Positional formula exceeds its budget.');
    text=text.trim();if(text==='odd')return {step:2,offset:1,fromEnd};if(text==='even')return {step:2,offset:0,fromEnd};
    const num=value=>{if(!/^[+-]?\d+$/.test(value))throw new Error('Invalid positional coefficient.');return integer(Number(value),-4096,4096);};
    const n=text.indexOf('n');if(n<0)return {step:0,offset:num(text),fromEnd};
    const prefix=text.slice(0,n).trimEnd(),suffix=text.slice(n+1).trim(),step=prefix===''||prefix==='+'?1:prefix==='-'?-1:num(prefix);
    if(suffix&&!/^[+-]\s*\d+$/.test(suffix))throw new Error('A positional offset requires a sign.');
    return {step,offset:suffix?(suffix[0]==='-'?-1:1)*num(suffix.slice(1).trim()):0,fromEnd};
  }
  function compound(depth){
    if(depth>4)throw new Error('Selector nesting budget exceeded.');
    const start=p,result={type:null,derived:false,name:null,classes:[],pseudos:[],not:[],positions:[]};
    if(/[A-Za-z_]/.test(source[p]??''))result.type=known(id());else take('*');
    while(p<source.length&&'.#:'.includes(source[p])){
      if(++predicates>64)throw new Error('Selector predicate budget exceeded.');
      const token=source[p++],value=id();
      if(token==='.') {if(result.classes.includes(value))throw new Error('Duplicate selector class.');result.classes.push(value);}
      else if(token==='#') {if(result.name!==null)throw new Error('Duplicate selector name.');result.name=value;}
      else if(value==='is'){if(result.type)throw new Error('Duplicate selector type.');require('(');white();result.type=known(id());white();require(')');result.derived=true;}
      else if(value==='not'){require('(');white();result.not.push(compound(depth+1));white();require(')');}
      else if(value==='nth-child'||value==='nth-last-child'){
        require('(');const start=p;while(p<source.length&&source[p]!==')')p++;const text=source.slice(start,p);require(')');
        const fromEnd=value==='nth-last-child';if(result.positions.some(v=>v.fromEnd===fromEnd))throw new Error('Duplicate positional selector.');
        result.positions.push(position(text,fromEnd));
      }else{if(!Object.hasOwn(stylePseudo,value)||result.pseudos.includes(value))throw new Error('Unsupported pseudoclass.');result.pseudos.push(value);}
    }
    if(p===start)throw new Error('Expected a selector predicate.');return result;
  }
  white();let relation='root';
  while(true){
    if(steps.length===16)throw new Error('Selector step budget exceeded.');steps.push({relation,predicate:compound(0)});
    const spaced=white();if(p===source.length)break;
    if(take('>'))relation='child';else if(take('/template/'))relation='template';else if(spaced)relation='descendant';else throw new Error('Invalid selector relationship.');
    white();if(p===source.length)throw new Error('Trailing selector relationship.');
  }
  const target=steps.at(-1).predicate.type;if(!target)throw new Error('The final selector needs a registered type.');
  const conditional=c=>!!(c.classes.length||c.pseudos.length||c.positions.length||c.not.some(conditional));
  return {target,steps,conditional:steps.some(step=>conditional(step.predicate))};
}
function uiSelectorIndex(value){
  const index=new Map();let ordinal=0;
  function walk(node,parent,position,count){
    const item={node,parent,position,count,start:ordinal++,end:0};index.set(node.key,item);
    node.children.forEach((child,i)=>walk(child,item,i+1,node.children.length));item.end=ordinal;
  }
  value.roots.forEach((node,i)=>walk(node,null,i+1,value.roots.length));return index;
}
function uiSelectorTargets(selector,owner,index,budget){
  const key=item=>'[data-ui-key="'+CSS.escape(item.node.key)+'"]';
  function predicate(c,item){
    if(++budget.work>262144)throw new Error('Logical selector work budget exceeded.');
    const p=item.node.properties;
    if(!uiSelectorType(item.node.type,c.type,c.derived)||c.name!==null&&p.Name!==c.name||c.classes.some(name=>!uiStyleClasses(p.Classes??'').includes(name)))return null;
    for(const position of c.positions){
      const n=position.fromEnd?item.count-item.position+1:item.position,difference=n-position.offset;
      if(position.step===0?difference!==0:difference/position.step<0||difference%position.step!==0)return null;
    }
    let css=c.pseudos.map(name=>stylePseudo[name]).join('');
    for(const negative of c.not){const excluded=predicate(negative,item);if(excluded==='')return null;if(excluded!==null)css+=':not('+excluded+')';}
    return css;
  }
  // A native template boundary never means an ordinary DOM descendant. The portable
  // fallback does not invent template parts or let these selectors target response children.
  if(selector.steps.some(step=>step.relation==='template'))return [];
  const cache=new Map();
  function match(step,item){
    if(!item)return [];
    const id=step+'\n'+item.node.key;if(cache.has(id))return cache.get(id);
    const own=predicate(selector.steps[step].predicate,item),result=[];cache.set(id,result);
    if(own===null)return result;
    if(step===0){result.push(key(item)+own);return result;}
    for(let parent=item.parent;parent;parent=selector.steps[step].relation==='child'?null:parent.parent){
      for(const prefix of match(step-1,parent)){
        const text=prefix+' '+key(item)+own;
        if(++budget.paths>32768||(budget.characters+=text.length)>2097152)throw new Error('Logical selector projection budget exceeded.');
        result.push(text);
      }
    }
    return result;
  }
  const scope=index.get(owner.key),result=new Set();
  for(const item of index.values())if(item.start>=scope.start&&item.start<scope.end)
    for(const path of match(selector.steps.length-1,item))result.add(':where('+path+')');
  return [...result];
}
