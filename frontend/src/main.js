import { createApp } from "vue";
import { ElAlert } from "element-plus/es/components/alert/index.mjs";
import { ElAutocomplete } from "element-plus/es/components/autocomplete/index.mjs";
import { ElButton } from "element-plus/es/components/button/index.mjs";
import { ElCard } from "element-plus/es/components/card/index.mjs";
import { ElCheckbox, ElCheckboxGroup } from "element-plus/es/components/checkbox/index.mjs";
import { ElCol } from "element-plus/es/components/col/index.mjs";
import { ElAside, ElContainer, ElHeader, ElMain } from "element-plus/es/components/container/index.mjs";
import { ElDatePicker } from "element-plus/es/components/date-picker/index.mjs";
import { ElDescriptions, ElDescriptionsItem } from "element-plus/es/components/descriptions/index.mjs";
import { ElDialog } from "element-plus/es/components/dialog/index.mjs";
import { ElDivider } from "element-plus/es/components/divider/index.mjs";
import { ElDrawer } from "element-plus/es/components/drawer/index.mjs";
import { ElDropdown, ElDropdownItem, ElDropdownMenu } from "element-plus/es/components/dropdown/index.mjs";
import { ElEmpty } from "element-plus/es/components/empty/index.mjs";
import { ElForm, ElFormItem } from "element-plus/es/components/form/index.mjs";
import { ElIcon } from "element-plus/es/components/icon/index.mjs";
import { ElInput } from "element-plus/es/components/input/index.mjs";
import { ElInputNumber } from "element-plus/es/components/input-number/index.mjs";
import { ElLoading } from "element-plus/es/components/loading/index.mjs";
import { ElMenu, ElMenuItem } from "element-plus/es/components/menu/index.mjs";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { ElPagination } from "element-plus/es/components/pagination/index.mjs";
import { ElPopconfirm } from "element-plus/es/components/popconfirm/index.mjs";
import { ElPopover } from "element-plus/es/components/popover/index.mjs";
import { ElRadioButton, ElRadioGroup } from "element-plus/es/components/radio/index.mjs";
import { ElRow } from "element-plus/es/components/row/index.mjs";
import { ElSegmented } from "element-plus/es/components/segmented/index.mjs";
import { ElOption, ElSelect } from "element-plus/es/components/select/index.mjs";
import { ElStatistic } from "element-plus/es/components/statistic/index.mjs";
import { ElSwitch } from "element-plus/es/components/switch/index.mjs";
import { ElTable, ElTableColumn } from "element-plus/es/components/table/index.mjs";
import { ElTabPane, ElTabs } from "element-plus/es/components/tabs/index.mjs";
import { ElTag } from "element-plus/es/components/tag/index.mjs";
import { ElTooltip } from "element-plus/es/components/tooltip/index.mjs";
import { ElUpload } from "element-plus/es/components/upload/index.mjs";
import "element-plus/es/components/alert/style/css.mjs";
import "element-plus/es/components/aside/style/css.mjs";
import "element-plus/es/components/autocomplete/style/css.mjs";
import "element-plus/es/components/button/style/css.mjs";
import "element-plus/es/components/card/style/css.mjs";
import "element-plus/es/components/checkbox/style/css.mjs";
import "element-plus/es/components/checkbox-group/style/css.mjs";
import "element-plus/es/components/col/style/css.mjs";
import "element-plus/es/components/container/style/css.mjs";
import "element-plus/es/components/date-picker/style/css.mjs";
import "element-plus/es/components/descriptions/style/css.mjs";
import "element-plus/es/components/descriptions-item/style/css.mjs";
import "element-plus/es/components/dialog/style/css.mjs";
import "element-plus/es/components/divider/style/css.mjs";
import "element-plus/es/components/drawer/style/css.mjs";
import "element-plus/es/components/dropdown/style/css.mjs";
import "element-plus/es/components/dropdown-item/style/css.mjs";
import "element-plus/es/components/dropdown-menu/style/css.mjs";
import "element-plus/es/components/empty/style/css.mjs";
import "element-plus/es/components/form/style/css.mjs";
import "element-plus/es/components/form-item/style/css.mjs";
import "element-plus/es/components/header/style/css.mjs";
import "element-plus/es/components/icon/style/css.mjs";
import "element-plus/es/components/input/style/css.mjs";
import "element-plus/es/components/input-number/style/css.mjs";
import "element-plus/es/components/loading/style/css.mjs";
import "element-plus/es/components/main/style/css.mjs";
import "element-plus/es/components/menu/style/css.mjs";
import "element-plus/es/components/menu-item/style/css.mjs";
import "element-plus/es/components/message/style/css.mjs";
import "element-plus/es/components/option/style/css.mjs";
import "element-plus/es/components/pagination/style/css.mjs";
import "element-plus/es/components/popconfirm/style/css.mjs";
import "element-plus/es/components/popover/style/css.mjs";
import "element-plus/es/components/radio-button/style/css.mjs";
import "element-plus/es/components/radio-group/style/css.mjs";
import "element-plus/es/components/row/style/css.mjs";
import "element-plus/es/components/segmented/style/css.mjs";
import "element-plus/es/components/select/style/css.mjs";
import "element-plus/es/components/statistic/style/css.mjs";
import "element-plus/es/components/switch/style/css.mjs";
import "element-plus/es/components/tab-pane/style/css.mjs";
import "element-plus/es/components/table/style/css.mjs";
import "element-plus/es/components/table-column/style/css.mjs";
import "element-plus/es/components/tabs/style/css.mjs";
import "element-plus/es/components/tag/style/css.mjs";
import "element-plus/es/components/tooltip/style/css.mjs";
import "element-plus/es/components/upload/style/css.mjs";
import App from "./App.vue";
import "./style.css";

const app = createApp(App);

const elementComponents = [
  ElAlert,
  ElAside,
  ElAutocomplete,
  ElButton,
  ElCard,
  ElCheckbox,
  ElCheckboxGroup,
  ElCol,
  ElContainer,
  ElDatePicker,
  ElDescriptions,
  ElDescriptionsItem,
  ElDialog,
  ElDivider,
  ElDrawer,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElHeader,
  ElIcon,
  ElInput,
  ElInputNumber,
  ElMain,
  ElMenu,
  ElMenuItem,
  ElOption,
  ElPagination,
  ElPopconfirm,
  ElPopover,
  ElRadioButton,
  ElRadioGroup,
  ElRow,
  ElSegmented,
  ElSelect,
  ElStatistic,
  ElSwitch,
  ElTabPane,
  ElTable,
  ElTableColumn,
  ElTabs,
  ElTag,
  ElTooltip,
  ElUpload
];

for (const component of elementComponents) {
  app.use(component);
}

app.use(ElLoading);
app.config.globalProperties.$message = ElMessage;
app.mount("#app");
